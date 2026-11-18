using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Stamps.Tests
{
    // Three 100 m tiles in a row along x, each with a 32 x 32 alphamap and two layers.
    public sealed class TextureRegionTests
    {
        private const int AlphamapResolution = 32;
        private const int TileCount = 3;

        private Scene _scene;
        private TerrainData[] _data;
        private UnityEngine.Terrain[] _terrains;
        private TerrainLayer[] _layers;
        private LoogaStampWorld _world;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Texture stamps require compute shaders.");
            }
            _scene = EditorSceneManager.NewPreviewScene();
            _layers = new[] { new TerrainLayer { name = "Layer A" }, new TerrainLayer { name = "Layer B" } };
            _data = new TerrainData[TileCount];
            _terrains = new UnityEngine.Terrain[TileCount];
            for (int i = 0; i < TileCount; i++)
            {
                _data[i] = new TerrainData { heightmapResolution = 33, alphamapResolution = AlphamapResolution };
                _data[i].size = new Vector3(100f, 200f, 100f);
                _data[i].terrainLayers = _layers;
                GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(_data[i]);
                tile.transform.position = new Vector3(i * 100f, 0f, 0f);
                SceneManager.MoveGameObjectToScene(tile, _scene);
                _terrains[i] = tile.GetComponent<UnityEngine.Terrain>();
            }
            for (int i = 0; i < TileCount; i++)
            {
                _terrains[i].SetNeighbors(i > 0 ? _terrains[i - 1] : null, null,
                    i < TileCount - 1 ? _terrains[i + 1] : null, null);
            }
            var root = new GameObject("Stamp World");
            SceneManager.MoveGameObjectToScene(root, _scene);
            _world = root.AddComponent<LoogaStampWorld>();
            _world.SetTerrains(_terrains);
        }

        [TearDown]
        public void TearDown()
        {
            if (_data == null) return;
            _world.ReleaseResources();
            EditorSceneManager.ClosePreviewScene(_scene);
            foreach (TerrainData data in _data)
            {
                Object.DestroyImmediate(data);
            }
            foreach (TerrainLayer layer in _layers)
            {
                Object.DestroyImmediate(layer);
            }
        }

        [Test]
        public void RegionUpdateEqualsFullRebuild()
        {
            AddStamp(0, new Vector3(150f, 0f, 50f), new Vector3(300f, 1f, 100f), FalloffShape.Global);
            LoogaTextureStamp box = AddStamp(1, new Vector3(60f, 0f, 50f), new Vector3(30f, 1f, 30f), FalloffShape.Box);
            box.Falloff.SetShape(FalloffShape.Box, new Vector2(1f, 0.5f), FalloffEasing.Linear);
            _world.RebuildTextures();
            byte[][] before = ReadAll();

            // Across the edge between tile 0 and tile 1. Tile 2 does not change.
            Rect old = box.GetCullRect(HeightMapping.MicroVerseParity);
            box.transform.position = new Vector3(105f, 0f, 50f);
            Rect region = Rect.MinMaxRect(old.xMin, old.yMin, box.GetCullRect(HeightMapping.MicroVerseParity).xMax,
                old.yMax);
            Assert.That(_world.UpdateTextureRegion(region), Is.EqualTo(2), "Terrains written");
            byte[][] updated = ReadAll();
            Assert.That(updated[2], Is.EqualTo(before[2]), "Tile outside the region");

            _world.RebuildTextures();
            byte[][] full = ReadAll();
            for (int i = 0; i < TileCount; i++)
            {
                Assert.That(updated[i], Is.EqualTo(full[i]), "Tile " + i);
            }
        }

        [Test]
        public void RegionUpdateWithoutStoredMapsRebuildsAll()
        {
            AddStamp(1, new Vector3(150f, 0f, 50f), new Vector3(300f, 1f, 100f), FalloffShape.Global);
            Assert.That(_world.UpdateTextureRegion(new Rect(10f, 10f, 5f, 5f)), Is.EqualTo(TileCount));
            Assert.That(_world.StoredTextureLayerBytes, Is.EqualTo(TileCount * 2L * AlphamapResolution *
                AlphamapResolution * 4));
        }

        private LoogaTextureStamp AddStamp(int layer, Vector3 position, Vector3 scale, FalloffShape shape)
        {
            var stamp = new GameObject("Stamp " + layer).AddComponent<LoogaTextureStamp>();
            stamp.transform.SetParent(_world.transform, false);
            stamp.transform.position = position;
            stamp.transform.localScale = scale;
            stamp.SetTexture(_layers[layer], 1f, false);
            stamp.Falloff.SetShape(shape, new Vector2(1f, 1f), FalloffEasing.Linear);
            return stamp;
        }

        // The first alphamap of each terrain, read from the GPU.
        private byte[][] ReadAll()
        {
            var maps = new byte[TileCount][];
            for (int i = 0; i < TileCount; i++)
            {
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(_data[i].alphamapTextures[0]);
                request.WaitForCompletion();
                Assert.That(request.hasError, Is.False, "Readback");
                maps[i] = request.GetData<byte>().ToArray();
            }
            return maps;
        }
    }
}
