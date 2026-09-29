using System.Threading;
using LoogaSoft.Instancing;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Instances.Tests
{
    public sealed class GpuDetailPlacementTests
    {
        [Test]
        public void GeneratedPlacementIsDeterministicAndSupportsRegionalSurfaceRefresh()
        {
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 8, 32) };
            data.SetDetailResolution(16, 8);
            var texture = new Texture2D(2, 2);
            data.detailPrototypes = new[]
            {
                new DetailPrototype
                {
                    prototypeTexture = texture,
                    renderMode = DetailRenderMode.GrassBillboard,
                    minWidth = 0.8f, maxWidth = 1.2f,
                    minHeight = 0.8f, maxHeight = 1.2f,
                    density = 1, noiseSeed = 19,
                    useDensityScaling = true
                }
            };
            var density = new int[16, 16];
            for (int z = 0; z < 8; z++) for (int x = 0; x < 8; x++) density[z, x] = 2;
            data.SetDetailLayer(0, 0, 0, density);
            GameObject terrainObject = UnityEngine.Terrain.CreateTerrainGameObject(data);
            try
            {
                var source = new GeneratedVegetationPageSource(terrainObject.GetComponent<UnityEngine.Terrain>(),
                    DetailGenerationBackend.Reference);
                var first = source.LoadAsync(Vector2Int.zero, false, CancellationToken.None).GetAwaiter().GetResult();
                var second = source.LoadAsync(Vector2Int.zero, false, CancellationToken.None).GetAwaiter().GetResult();
                Assert.Greater(first.Length, 0);
                Assert.AreEqual(first.Length, second.Length);
                for (int i = 0; i < first.Length; i++) Assert.AreEqual(first[i].LocalTransform, second[i].LocalTransform);
                uint revision = source.SurfaceRevision;
                source.RefreshHeights(new RectInt(0, 0, 2, 2));
                Assert.AreEqual(revision + 1, source.SurfaceRevision);
                source.RefreshHoles(new RectInt(-10, -10, 1, 1));
                Assert.AreEqual(revision + 1, source.SurfaceRevision);
            }
            finally
            {
                Object.DestroyImmediate(terrainObject);
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(texture);
            }
        }

        // Sloped heights, a hole with edge padding, fractional density, jitter, alignment and a moved origin.
        [Test]
        public void GpuPlacementMatchesReferencePlacement()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("GPU detail placement requires compute shaders.");
            }
            var data = new TerrainData { heightmapResolution = 65, size = new Vector3(64, 30, 64) };
            var heights = new float[65, 65];
            for (int z = 0; z < 65; z++)
            {
                for (int x = 0; x < 65; x++)
                {
                    heights[z, x] = 0.3f + 0.2f * Mathf.Sin(x * 0.2f) * Mathf.Cos(z * 0.15f) + 0.001f * x;
                }
            }
            data.SetHeights(0, 0, heights);
            var holes = new bool[64, 64];
            for (int z = 0; z < 64; z++)
            {
                for (int x = 0; x < 64; x++)
                {
                    holes[z, x] = !(x >= 20 && x < 28 && z >= 30 && z < 36);
                }
            }
            data.SetHoles(0, 0, holes);
            // Kubera terrains use instance count mode. New TerrainData can default to coverage mode.
            data.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);
            data.SetDetailResolution(32, 16);
            var texture = new Texture2D(2, 2);
            data.detailPrototypes = new[]
            {
                new DetailPrototype
                {
                    prototypeTexture = texture,
                    renderMode = DetailRenderMode.GrassBillboard,
                    minWidth = 0.6f, maxWidth = 1.4f,
                    minHeight = 0.8f, maxHeight = 1.9f,
                    noiseSeed = 1234, positionJitter = 0.8f,
                    alignToGround = 0.6f, holeEdgePadding = 1.5f,
                    useDensityScaling = true
                }
            };
            var density = new int[32, 32];
            for (int z = 0; z < 32; z++)
            {
                for (int x = 0; x < 32; x++)
                {
                    density[z, x] = (x * 3 + z) % 5;
                }
            }
            data.SetDetailLayer(0, 0, 0, density);
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            GameObject terrainObject = UnityEngine.Terrain.CreateTerrainGameObject(data);
            GpuDetailGenerator generator = null;
            try
            {
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(terrainObject, scene);
                terrainObject.transform.position = new Vector3(100, 5, -50);
                var terrain = terrainObject.GetComponent<UnityEngine.Terrain>();
                terrain.detailObjectDensity = 0.7f;
                var reference = new GeneratedVegetationPageSource(terrain, DetailGenerationBackend.Reference);
                generator = new GpuDetailGenerator(terrain);
                var patches = new[] { new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(0, 1), new Vector2Int(1, 1) };
                var firsts = new int[patches.Length];
                var counts = new int[patches.Length];
                for (int i = 0; i < patches.Length; i++)
                {
                    counts[i] = generator.AddPage(patches[i], 0, out firsts[i]);
                }
                generator.Dispatch();
                var rows = new Vector4[generator.PendingPlacements * 3];
                generator.Placements.GetData(rows, 0, 0, rows.Length);
                int rejected = 0;
                for (int i = 0; i < patches.Length; i++)
                {
                    VegetationPageRecord[] expected = reference.LoadAsync(patches[i], false, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    int matched = 0;
                    for (int output = firsts[i]; output < firsts[i] + counts[i]; output++)
                    {
                        Vector4 row0 = rows[output * 3];
                        Vector4 row1 = rows[output * 3 + 1];
                        Vector4 row2 = rows[output * 3 + 2];
                        if (row0 == Vector4.zero && row1 == Vector4.zero && row2 == Vector4.zero)
                        {
                            rejected++;
                            continue;
                        }
                        Assert.Less(matched, expected.Length, $"Patch {patches[i]} has extra placements.");
                        Matrix4x4 world = Matrix4x4.Translate(terrainObject.transform.position) *
                            expected[matched].LocalTransform;
                        for (int row = 0; row < 3; row++)
                        {
                            Vector4 actual = row == 0 ? row0 : row == 1 ? row1 : row2;
                            for (int column = 0; column < 4; column++)
                            {
                                Assert.That(actual[column], Is.EqualTo(world[row, column]).Within(0.001f),
                                    $"Patch {patches[i]}, placement {matched}, row {row}, column {column}");
                            }
                        }
                        matched++;
                    }
                    Assert.AreEqual(expected.Length, matched, $"Patch {patches[i]} placement count");
                }
                Assert.Greater(rejected, 0, "The hole must reject some candidates.");
                Assert.Greater(generator.PendingPlacements - rejected, 800);
            }
            finally
            {
                generator?.Dispose();
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(texture);
            }
        }
    }
}
