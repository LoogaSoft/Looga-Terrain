using System.Collections.Generic;
using LoogaSoft.Terrain.Grass;
using NUnit.Framework;
using UnityEngine;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class ProceduralGrassTileTests
    {
        private const float CellSize = 0.25f;
        private const float FullDensityDistance = 8f;
        private const float BladeDistance = 40f;

        [TestCase(0f, 0f)]
        [TestCase(3.3f, -7.9f)]
        [TestCase(-123.4f, 517.6f)]
        [TestCase(15.99f, 16.01f)]
        public void RingsVisitEachVisibleLatticePointOnce(float cameraX, float cameraZ)
        {
            var tiles = new List<GrassTile>();
            var camera = new Vector2(cameraX, cameraZ);
            ProceduralGrassTiles.Build(camera, CellSize, FullDensityDistance, BladeDistance, tiles);
            int ringCount = ProceduralGrassTiles.RingCount(FullDensityDistance, BladeDistance);

            var visits = new Dictionary<long, int>();
            foreach (GrassTile tile in tiles)
            {
                int step = 1 << tile.Level;
                for (int z = 0; z < ProceduralGrassTiles.TileLanes; z++)
                {
                    for (int x = 0; x < ProceduralGrassTiles.TileLanes; x++)
                    {
                        long key = Key(tile.StartX + x * step, tile.StartZ + z * step);
                        visits.TryGetValue(key, out int count);
                        visits[key] = count + 1;
                    }
                }
            }
            foreach (KeyValuePair<long, int> visit in visits)
            {
                Assert.AreEqual(1, visit.Value, "A lattice point was visited twice.");
            }

            // Every lattice point within the visible distance of its level must be visited.
            int reach = Mathf.CeilToInt(BladeDistance / CellSize) + 2;
            int centreX = Mathf.FloorToInt(cameraX / CellSize);
            int centreZ = Mathf.FloorToInt(cameraZ / CellSize);
            for (int z = centreZ - reach; z <= centreZ + reach; z++)
            {
                for (int x = centreX - reach; x <= centreX + reach; x++)
                {
                    int level = Mathf.Min(Level(x, z), ringCount - 1);
                    float visible = ProceduralGrassTiles.VisibleDistance(level, ringCount, FullDensityDistance,
                        BladeDistance);
                    var position = new Vector2((x + 0.5f) * CellSize, (z + 0.5f) * CellSize);
                    // The jitter moves a point by up to half a cell, so points near the limit can go either way.
                    if (Vector2.Distance(position, camera) > visible - CellSize) continue;
                    Assert.IsTrue(visits.ContainsKey(Key(x, z)), "A visible lattice point was not visited.");
                }
            }
        }

        [Test]
        public void RingCountReachesTheBladeDistance()
        {
            Assert.AreEqual(1, ProceduralGrassTiles.RingCount(25f, 20f));
            Assert.AreEqual(3, ProceduralGrassTiles.RingCount(25f, 120f));
            Assert.AreEqual(120f, ProceduralGrassTiles.VisibleDistance(2, 3, 25f, 120f));
            Assert.AreEqual(100f, ProceduralGrassTiles.VisibleDistance(1, 3, 25f, 120f));
            Assert.AreEqual(50f, ProceduralGrassTiles.VisibleDistance(0, 3, 25f, 120f));
        }

        private static long Key(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        private static int Level(int x, int z)
        {
            int level = 0;
            while (level < 30 && ((x >> level) & 1) == 0 && ((z >> level) & 1) == 0)
            {
                level++;
            }
            return level;
        }
    }
}
