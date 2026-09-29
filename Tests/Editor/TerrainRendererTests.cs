using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class TerrainRendererTests
    {
        private const string PackagePath = "Packages/com.loogasoft.loogaterrain";

        [Test]
        public void SelectionCoversEveryCellExactlyOnce()
        {
            Selection result = Select(new Vector3(64f, 30f, -80f), false);
            var coverage = new int[32 * 32];
            foreach (Vector4 patch in result.Patches)
            {
                int size = (int)patch.z;
                Assert.That(Mathf.IsPowerOfTwo(size));
                Assert.That((int)patch.x % size, Is.Zero);
                Assert.That((int)patch.y % size, Is.Zero);
                for (int z = (int)patch.y; z < patch.y + size; z++)
                {
                    for (int x = (int)patch.x; x < patch.x + size; x++)
                    {
                        coverage[z * 32 + x]++;
                        Assert.That(result.LodMap[z * 32 + x], Is.EqualTo(size));
                    }
                }
            }
            Assert.That(coverage, Is.All.EqualTo(1));
        }

        [Test]
        public void LodTransitionsHaveNoOpenInteriorEdges()
        {
            Selection selection = Select(new Vector3(64f, 30f, -80f), false);
            ComputeShader shader = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(PackagePath + "/Tests/Editor/GeometrySampling.compute"));
            using var patches = new GraphicsBuffer(GraphicsBuffer.Target.Structured, selection.Patches.Length, 16);
            using var lod = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1024, 4);
            using var vertices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, selection.Patches.Length * 289, 8);
            Mesh mesh = PatchMesh.Create(16);
            try
            {
                patches.SetData(selection.Patches);
                lod.SetData(selection.LodMap);
                shader.SetBuffer(0, "_LoogaPatches", patches);
                shader.SetBuffer(0, "_LoogaLodMap", lod);
                BindNeighborMaps(shader, lod, 0);
                shader.SetBuffer(0, "_Vertices", vertices);
                shader.SetInt("_LoogaPatchCount", 32);
                shader.SetInt("_LoogaPatchResolution", 16);
                shader.SetInt("_SelectedCount", selection.Patches.Length);
                shader.Dispatch(0, (vertices.count + 63) / 64, 1, 1);
                var positions = new Vector2[vertices.count];
                vertices.GetData(positions);
                var edges = new System.Collections.Generic.Dictionary<(int, int), int>();
                int[] triangles = mesh.triangles;
                for (int patch = 0; patch < selection.Patches.Length; patch++)
                {
                    for (int triangle = 0; triangle < triangles.Length; triangle += 3)
                    {
                        Vector2 a = positions[patch * 289 + triangles[triangle]] * 512f;
                        Vector2 b = positions[patch * 289 + triangles[triangle + 1]] * 512f;
                        Vector2 c = positions[patch * 289 + triangles[triangle + 2]] * 512f;
                        if (Mathf.Abs((b.x-a.x)*(c.y-a.y)-(b.y-a.y)*(c.x-a.x)) < 0.001f) continue;
                        AddEdge(edges, a, b);
                        AddEdge(edges, b, c);
                        AddEdge(edges, c, a);
                    }
                }
                foreach (var edge in edges)
                {
                    int ax = edge.Key.Item1 % 513, az = edge.Key.Item1 / 513;
                    int bx = edge.Key.Item2 % 513, bz = edge.Key.Item2 / 513;
                    bool boundary = (ax == bx && (ax == 0 || ax == 512)) || (az == bz && (az == 0 || az == 512));
                    Assert.That(edge.Value, Is.EqualTo(boundary ? 1 : 2), $"Open edge ({ax},{az}) to ({bx},{bz}).");
                }
            }
            finally
            {
                Object.DestroyImmediate(shader);
                Object.DestroyImmediate(mesh);
            }
        }

        private static void AddEdge(System.Collections.Generic.Dictionary<(int, int), int> edges, Vector2 a, Vector2 b)
        {
            int start = Mathf.RoundToInt(a.y) * 513 + Mathf.RoundToInt(a.x);
            int end = Mathf.RoundToInt(b.y) * 513 + Mathf.RoundToInt(b.x);
            var key = start < end ? (start, end) : (end, start);
            edges.TryGetValue(key, out int count);
            edges[key] = count + 1;
        }

        [Test]
        public void SelectionReducesDistantPatches()
        {
            Selection near = Select(new Vector3(64f, 20f, 64f), false);
            Selection far = Select(new Vector3(64f, 200f, -2000f), false);
            Assert.That(near.Patches.Length, Is.GreaterThan(far.Patches.Length));
            Assert.That(far.VisibleCount, Is.EqualTo(far.Patches.Length));
        }

        [Test]
        public void CameraCullingRetainsOffscreenShadowCasters()
        {
            Selection result = Select(new Vector3(64f, 20f, -100f), true);
            Assert.That(result.VisibleCount, Is.Zero);
            Assert.That(result.Patches.Length, Is.GreaterThan(0));
        }

        [Test]
        public void GpuHeightDecodeMatchesNativeTerrain()
        {
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(32f, 400f, 32f) };
            ComputeShader shader = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(PackagePath + "/Tests/Editor/HeightSampling.compute"));
            try
            {
                var heights = new float[33, 33];
                for (int z = 0; z < 33; z++)
                {
                    for (int x = 0; x < 33; x++)
                    {
                        heights[z, x] = (x + z) / 64f;
                    }
                }
                data.SetHeights(0, 0, heights);
                using var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 33 * 33, sizeof(float));
                shader.SetTexture(0, "_LoogaHeightmap", data.heightmapTexture);
                shader.SetVector("_LoogaHeightInfo", new Vector4(33f, 65535f / 32766f, 0f, 0f));
                shader.SetVector("_LoogaOrigin", new Vector4(0f, -125f, 0f, 0f));
                shader.SetVector("_LoogaSize", data.size);
                shader.SetBuffer(0, "_Samples", buffer);
                shader.Dispatch(0, 5, 5, 1);
                var sampled = new float[33 * 33];
                buffer.GetData(sampled);
                for (int z = 0; z < 33; z++)
                {
                    for (int x = 0; x < 33; x++)
                    {
                        Assert.That(sampled[z * 33 + x], Is.EqualTo(data.GetHeight(x, z) - 125f).Within(0.025f));
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(shader);
                Object.DestroyImmediate(data);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void DisableRestoresVisibilityAndPreservesCollider(bool originalVisibility)
        {
            var data = new TerrainData { heightmapResolution = 33 };
            GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(data);
            tile.hideFlags = HideFlags.HideAndDontSave;
            var material = new Material(AssetDatabase.LoadAssetAtPath<Shader>(PackagePath + "/Runtime/Shaders/TerrainLit.shader"));
            try
            {
                var terrain = tile.GetComponent<UnityEngine.Terrain>();
                TerrainCollider collider = tile.GetComponent<TerrainCollider>();
                terrain.drawHeightmap = originalVisibility;
                TerrainRenderer renderer = tile.AddComponent<TerrainRenderer>();
                renderer.Configure(AssetDatabase.LoadAssetAtPath<ComputeShader>(PackagePath + "/Runtime/Shaders/TerrainSelection.compute"), material, 32);
                Assert.That(renderer.IsReady, Is.True, renderer.Diagnostic);
                Assert.That(terrain.drawHeightmap, Is.False);
                Assert.That(collider.enabled, Is.True);
                Assert.That(collider.terrainData, Is.SameAs(data));
                renderer.enabled = false;
                Assert.That(terrain.drawHeightmap, Is.EqualTo(originalVisibility));
                Assert.That(renderer.IsReady, Is.False);
                renderer.enabled = true;
                Assert.That(renderer.IsReady, Is.True);
                renderer.enabled = false;
                Assert.That(terrain.drawHeightmap, Is.EqualTo(originalVisibility));
            }
            finally
            {
                Object.DestroyImmediate(tile);
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void ErrorSelectionKeepsFlatTileAsOnePatch()
        {
            Selection result = Select(new Vector3(64f, 30f, 64f), false, true);
            Assert.That(result.Patches.Length, Is.EqualTo(1));
            Assert.That(result.Root.x, Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(result.Root.y, Is.EqualTo(0.25f).Within(0.0001f));
            Assert.That(result.Root.z, Is.LessThan(0.00001f));
        }

        [Test]
        public void ErrorSelectionPreservesNarrowHeightPeak()
        {
            Selection result = Select(new Vector3(64f, 30f, 64f), false, true, true);
            Assert.That(result.Patches.Length, Is.GreaterThan(1));
            Assert.That(result.Root.y, Is.EqualTo(0.9f).Within(0.0001f));
            Assert.That(result.Root.z, Is.GreaterThan(0.5f));
        }

        [Test]
        public void OrthographicErrorSelectionDoesNotDependOnCameraDistance()
        {
            Selection near = Select(new Vector3(64f, 30f, 64f), false, true, true, true);
            Selection far = Select(new Vector3(64f, 3000f, 64f), false, true, true, true);
            Assert.That(near.Patches.Length, Is.EqualTo(far.Patches.Length));
        }

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(1, false)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        [TestCase(2, true)]
        [TestCase(3, false)]
        [TestCase(3, true)]
        public void TileBoundaryVerticesMatchCoarseNeighbor(int direction, bool useOffsets)
        {
            ComputeShader shader = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(PackagePath + "/Tests/Editor/GeometrySampling.compute"));
            using var patches = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 64, 16);
            using var lod = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2048, 4);
            using var coarse = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2048, 4);
            using var vertices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 32 * 289, 8);
            try
            {
                var patchData = new Vector4[32];
                var sizes = new uint[1024];
                var coarseSizes = new uint[1024];
                for (int i = 0; i < sizes.Length; i++)
                {
                    sizes[i] = 1;
                    coarseSizes[i] = 32;
                }
                for (int i = 0; i < patchData.Length; i++)
                {
                    patchData[i] = direction < 2 ? new Vector4(direction == 0 ? 0 : 31, i, 1, 0)
                        : new Vector4(i, direction == 2 ? 0 : 31, 1, 0);
                }
                patches.SetData(new Vector4[64]);
                patches.SetData(patchData, 0, useOffsets ? 32 : 0, 32);
                lod.SetData(coarseSizes);
                lod.SetData(sizes, 0, useOffsets ? 1024 : 0, 1024);
                coarse.SetData(sizes);
                coarse.SetData(coarseSizes, 0, useOffsets ? 1024 : 0, 1024);
                shader.SetBuffer(0, "_LoogaPatches", patches);
                shader.SetBuffer(0, "_LoogaLodMap", lod);
                BindNeighborMaps(shader, coarse, 1 << direction);
                shader.SetBuffer(0, "_Vertices", vertices);
                shader.SetInt("_LoogaPatchOffset", useOffsets ? 32 : 0);
                shader.SetInt("_LoogaLodOffset", useOffsets ? 1024 : 0);
                shader.SetVector("_LoogaNeighborOffsets", Vector4.one * (useOffsets ? 1024 : 0));
                shader.SetInt("_LoogaPatchCount", 32);
                shader.SetInt("_LoogaPatchResolution", 16);
                shader.SetInt("_SelectedCount", 32);
                shader.Dispatch(0, (vertices.count + 63) / 64, 1, 1);
                var positions = new Vector2[vertices.count];
                vertices.GetData(positions);
                var unique = new System.Collections.Generic.HashSet<int>();
                foreach (Vector2 position in positions)
                {
                    float edge = direction < 2 ? position.x : position.y;
                    float along = direction < 2 ? position.y : position.x;
                    if (Mathf.Abs(edge - (direction % 2 == 0 ? 0 : 1)) > 0.00001f) continue;
                    Assert.That(along * 16, Is.EqualTo(Mathf.Round(along * 16)).Within(0.0001f));
                    unique.Add(Mathf.RoundToInt(along * 16));
                }
                Assert.That(unique.Count, Is.EqualTo(17));
            }
            finally
            {
                Object.DestroyImmediate(shader);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BatchSelectionMatchesIndependentTilesAndResetsCounters(bool orthographic)
        {
            Vector3 cameraPosition = new(64, 30, 64);
            var expected = new Selection[4];
            var parameters = new Vector4[12];
            for (int tile = 0; tile < 4; tile++)
            {
                Vector3 origin = new(tile * 128, tile * 5, 0);
                expected[tile] = Select(cameraPosition - origin, tile % 2 != 0, tile >= 2, tile % 2 != 0, orthographic);
                parameters[tile * 3] = origin;
                parameters[tile * 3 + 1] = new Vector4(128, 40, 128, 0);
                parameters[tile * 3 + 2] = new Vector4(128, 2, tile >= 2 ? 1 : 0, tile % 2);
            }
            ComputeShader shader = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(PackagePath + "/Runtime/Shaders/TerrainSelection.compute"));
            using var source = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1365, 16);
            using var nodes = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1365 * 4, 16);
            using var tiles = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 48);
            using var lod = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4096, 4);
            using var visible = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4096, 16);
            using var shadows = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4096, 16);
            using var visibleArgs = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments, 4, 20);
            using var shadowArgs = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.IndirectArguments, 4, 20);
            using var cameraData = new GraphicsBuffer(GraphicsBuffer.Target.Constant, 8, 16);
            try
            {
                int copy = shader.FindKernel("CopyBatchNodes");
                int reset = shader.FindKernel("ResetBatchArguments");
                int select = shader.FindKernel("SelectBatch");
                shader.SetInt("_NodeCount", 1365);
                shader.SetInt("_PatchCount", 32);
                shader.SetInt("_FixedEdges", 0);
                shader.SetInt("_BatchTileCount", 4);
                shader.SetInt("_BatchIndexCount", 1536);
                shader.SetBuffer(copy, "_NodeData", source);
                shader.SetBuffer(copy, "_BatchNodeData", nodes);
                for (int tile = 0; tile < 4; tile++)
                {
                    source.SetData(expected[tile].Nodes);
                    shader.SetInt("_BatchNodeOffset", tile * 1365);
                    shader.Dispatch(copy, 22, 1, 1);
                }
                tiles.SetData(parameters);
                cameraData.SetData(new[] { (Vector4)cameraPosition, new Vector4(1000, orthographic ? 1 : 0, 0, 0),
                    new Vector4(0, 0, -1, -1000), Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero });
                shader.SetConstantBuffer(Shader.PropertyToID("CameraParameters"), cameraData, 0, 128);
                shader.SetBuffer(select, "_BatchTiles", tiles);
                shader.SetBuffer(select, "_BatchNodeData", nodes);
                shader.SetBuffer(select, "_BatchLod", lod);
                shader.SetBuffer(select, "_BatchVisible", visible);
                shader.SetBuffer(select, "_BatchShadows", shadows);
                shader.SetBuffer(select, "_BatchVisibleArguments", visibleArgs);
                shader.SetBuffer(select, "_BatchShadowArguments", shadowArgs);
                shader.SetBuffer(reset, "_BatchVisibleArguments", visibleArgs);
                shader.SetBuffer(reset, "_BatchShadowArguments", shadowArgs);
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    shader.Dispatch(reset, 1, 1, 1);
                    shader.Dispatch(select, 4, 4, 4);
                    var visibleCounts = new uint[20];
                    var shadowCounts = new uint[20];
                    visibleArgs.GetData(visibleCounts);
                    shadowArgs.GetData(shadowCounts);
                    for (int tile = 0; tile < 4; tile++)
                    {
                        Assert.That(visibleCounts[tile * 5], Is.EqualTo(1536));
                        Assert.That(visibleCounts[tile * 5 + 1], Is.EqualTo(expected[tile].VisibleCount));
                        Assert.That(shadowCounts[tile * 5 + 1], Is.EqualTo(expected[tile].Patches.Length));
                        for (int field = 2; field < 5; field++)
                        {
                            Assert.That(visibleCounts[tile * 5 + field], Is.Zero);
                            Assert.That(shadowCounts[tile * 5 + field], Is.Zero);
                        }
                        var patches = new Vector4[expected[tile].Patches.Length];
                        shadows.GetData(patches, 0, tile * 1024, patches.Length);
                        CollectionAssert.AreEquivalent(expected[tile].Patches, patches);
                        var sizes = new uint[1024];
                        lod.GetData(sizes, 0, tile * 1024, 1024);
                        CollectionAssert.AreEqual(expected[tile].LodMap, sizes);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(shader);
            }
        }

        private static void BindNeighborMaps(ComputeShader shader, GraphicsBuffer map, int mask)
        {
            shader.SetBuffer(0, "_LoogaLodLeft", map);
            shader.SetBuffer(0, "_LoogaLodRight", map);
            shader.SetBuffer(0, "_LoogaLodBottom", map);
            shader.SetBuffer(0, "_LoogaLodTop", map);
            shader.SetInt("_LoogaNeighborMask", mask);
        }

        private static Selection Select(Vector3 cameraPosition, bool cull, bool useError = false, bool spike = false, bool orthographic = false)
        {
            ComputeShader shader = Object.Instantiate(AssetDatabase.LoadAssetAtPath<ComputeShader>(PackagePath + "/Runtime/Shaders/TerrainSelection.compute"));
            using var visible = new GraphicsBuffer(GraphicsBuffer.Target.Append, 1024, 16);
            using var shadows = new GraphicsBuffer(GraphicsBuffer.Target.Append, 1024, 16);
            using var lod = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1024, 4);
            using var count = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, 4);
            using var nodes = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1365, 16);
            using var cameraData = new GraphicsBuffer(GraphicsBuffer.Target.Constant, 8, 16);
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(128f, 40f, 128f) };
            try
            {
                var heights = new float[33, 33];
                for (int z = 0; z < 33; z++)
                {
                    for (int x = 0; x < 33; x++)
                    {
                        heights[z, x] = 0.25f;
                    }
                }
                if (spike)
                {
                    heights[15, 17] = 0.9f;
                }
                data.SetHeights(0, 0, heights);
                shader.SetBuffer(0, "_NodeData", nodes);
                shader.SetBuffer(1, "_NodeData", nodes);
                shader.SetBuffer(2, "_NodeData", nodes);
                shader.SetTexture(1, "_SourceHeight", data.heightmapTexture);
                shader.SetInt("_HeightResolution", 33);
                shader.SetInt("_NodeCount", nodes.count);
                shader.SetInt("_PatchCount", 32);
                shader.Dispatch(2, (nodes.count + 63) / 64, 1, 1);
                for (int level = 0; level <= 5; level++)
                {
                    shader.SetInt("_BuildLevel", level);
                    shader.SetInt("_BuildGroups", 1);
                    shader.Dispatch(1, 32 >> level, 32 >> level, 1);
                }
                shader.SetVector("_TerrainOrigin", Vector4.zero);
                shader.SetVector("_TerrainSize", new Vector4(128f, 40f, 128f, 0f));
                cameraData.SetData(new[] { (Vector4)cameraPosition, new Vector4(1000f, orthographic ? 1 : 0, 0, 0),
                    new Vector4(0f, 0f, -1f, -1000f), Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero, Vector4.zero });
                shader.SetConstantBuffer(Shader.PropertyToID("CameraParameters"), cameraData, 0, 128);
                shader.SetFloat("_TargetPixels", 128f);
                shader.SetInt("_PatchCount", 32);
                shader.SetInt("_UseError", useError ? 1 : 0);
                shader.SetInt("_FixedEdges", 0);
                shader.SetFloat("_ErrorPixels", 2f);
                shader.SetInt("_Cull", cull ? 1 : 0);
                visible.SetCounterValue(0);
                shadows.SetCounterValue(0);
                shader.SetBuffer(0, "_VisiblePatches", visible);
                shader.SetBuffer(0, "_ShadowPatches", shadows);
                shader.SetBuffer(0, "_LodMap", lod);
                shader.Dispatch(0, 4, 4, 1);
                var counts = new uint[1];
                GraphicsBuffer.CopyCount(visible, count, 0);
                count.GetData(counts);
                uint visibleCount = counts[0];
                GraphicsBuffer.CopyCount(shadows, count, 0);
                count.GetData(counts);
                var patches = new Vector4[counts[0]];
                shadows.GetData(patches, 0, 0, patches.Length);
                var sizes = new uint[1024];
                lod.GetData(sizes);
                var bounds = new Vector4[nodes.count];
                nodes.GetData(bounds);
                return new Selection { Patches = patches, LodMap = sizes, VisibleCount = visibleCount, Root = bounds[bounds.Length - 1], Nodes = bounds };
            }
            finally
            {
                Object.DestroyImmediate(shader);
                Object.DestroyImmediate(data);
            }
        }

        private struct Selection
        {
            public Vector4 Root;
            public Vector4[] Patches;
            public Vector4[] Nodes;
            public uint[] LodMap;
            public uint VisibleCount;
        }
    }
}
