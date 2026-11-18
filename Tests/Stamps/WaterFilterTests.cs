using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Stamps.Tests
{
    // One 100 m tile, flat ground at 10 m, 128 x 128 alphamap and details. Water covers x 20 to 50 m over the whole
    // tile depth, so the distance past the water edge east of it is x - 50 m.
    public sealed class WaterFilterTests
    {
        private const int Resolution = 128;
        private const float Ground = 10f;
        private Scene _scene;
        private TerrainData _data;
        private UnityEngine.Terrain _terrain;
        private TerrainLayer[] _layers;
        private Texture2D _grass;
        private Mesh _mesh;
        private LoogaStampWorld _world;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Water filters require compute shaders.");
            }
            _scene = EditorSceneManager.NewPreviewScene();
            _layers = new[] { new TerrainLayer { name = "Ground" }, new TerrainLayer { name = "Shore" } };
            _grass = new Texture2D(4, 4);
            _data = new TerrainData { heightmapResolution = 129, alphamapResolution = Resolution };
            _data.size = new Vector3(100f, 200f, 100f);
            _data.terrainLayers = _layers;
            _data.SetDetailResolution(Resolution, 16);
            _data.SetDetailScatterMode(DetailScatterMode.CoverageMode);
            _data.detailPrototypes = new[]
            {
                new DetailPrototype { prototypeTexture = _grass, renderMode = DetailRenderMode.GrassBillboard }
            };
            var heights = new float[129, 129];
            for (int z = 0; z < 129; z++)
            {
                for (int x = 0; x < 129; x++)
                {
                    heights[z, x] = Ground / 200f;
                }
            }
            _data.SetHeights(0, 0, heights);
            GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(_data);
            SceneManager.MoveGameObjectToScene(tile, _scene);
            _terrain = tile.GetComponent<UnityEngine.Terrain>();
            var root = new GameObject("Stamp World");
            SceneManager.MoveGameObjectToScene(root, _scene);
            _world = root.AddComponent<LoogaStampWorld>();
            _world.SetTerrains(new[] { _terrain });
        }

        [TearDown]
        public void TearDown()
        {
            if (_data == null) return;
            _world.ReleaseResources();
            EditorSceneManager.ClosePreviewScene(_scene);
            Object.DestroyImmediate(_data);
            Object.DestroyImmediate(_grass);
            Object.DestroyImmediate(_mesh);
            foreach (TerrainLayer layer in _layers)
            {
                Object.DestroyImmediate(layer);
            }
        }

        [Test]
        public void EdgeBandPaintsNearAndInsideTheWater()
        {
            LoogaTextureStamp stamp = AddShoreStamp(CreateArea(9f));
            Band(stamp.WaterFilter.EdgeBand, -StampWaterFilter.Unlimited, 5f, 0f);
            byte[][] maps = EvaluateTextures();
            Assert.That(Shore(maps, 52.5f), Is.EqualTo(255), "2.5 m past the edge");
            Assert.That(Shore(maps, 30f), Is.EqualTo(255), "Inside the water");
            Assert.That(Shore(maps, 60f), Is.EqualTo(0), "10 m past the edge");
        }

        // Ground 10 m, water 9 m: 1 m above the water. Water 9.8 m: 0.2 m above the water.
        [TestCase(9f, 0)]
        [TestCase(9.8f, 255)]
        public void LowGroundNeedsBothDistanceAndHeight(float level, int expected)
        {
            LoogaTextureStamp stamp = AddShoreStamp(CreateArea(level));
            Band(stamp.WaterFilter.EdgeBand, -StampWaterFilter.Unlimited, 3f, 0f);
            Band(stamp.WaterFilter.LowGroundDistance, -StampWaterFilter.Unlimited, 20f, 0f);
            Band(stamp.WaterFilter.LowGroundHeight, -StampWaterFilter.Unlimited, 0.5f, 0f);
            byte[][] maps = EvaluateTextures();
            Assert.That(Shore(maps, 52f), Is.EqualTo(255), "Edge band");
            Assert.That(Shore(maps, 60f), Is.EqualTo(expected), "Low ground band only");
            Assert.That(Shore(maps, 75f), Is.EqualTo(0), "Beyond both bands");
        }

        [Test]
        public void InverseBandKeepsFarGroundAndFadesNearTheWater()
        {
            LoogaTextureStamp stamp = AddShoreStamp(CreateArea(9f));
            Band(stamp.WaterFilter.EdgeBand, 14f, StampWaterFilter.Unlimited, 10f);
            byte[][] maps = EvaluateTextures();
            Assert.That(Shore(maps, 52f), Is.EqualTo(0), "2 m past the edge");
            Assert.That(Shore(maps, 59f), Is.InRange(100, 155), "Half way through the fade");
            Assert.That(Shore(maps, 90f), Is.EqualTo(255), "Beyond the field reach");
            Assert.That(Shore(maps, 30f), Is.EqualTo(0), "Inside the water");
        }

        [Test]
        public void ClearStampClearsDetailsNearTheWater()
        {
            LoogaWaterArea area = CreateArea(9f);
            var detail = new GameObject("Detail").AddComponent<LoogaDetailStamp>();
            detail.transform.SetParent(_world.transform, false);
            detail.transform.localScale = new Vector3(400f, 100f, 400f);
            detail.Falloff.SetShape(FalloffShape.Global, Vector2.one, FalloffEasing.Linear);
            detail.SetDetail(0, 1f, new Vector2(0f, 999999f), false, true);
            var clear = new GameObject("Clear").AddComponent<LoogaClearStamp>();
            clear.transform.SetParent(_world.transform, false);
            clear.transform.localScale = new Vector3(400f, 100f, 400f);
            clear.Falloff.SetShape(FalloffShape.Global, Vector2.one, FalloffEasing.Linear);
            clear.SetClear(true, true, true, 1f, false);
            clear.WaterFilter.SetArea(area);
            Band(clear.WaterFilter.EdgeBand, -StampWaterFilter.Unlimited, 5f, 0f);
            StampPlacements placements = _world.EvaluatePlacements();
            Assert.That(placements.TryGetDetailLayer(_terrain, 0, out byte[] values), Is.True);
            int row = Resolution / 2;
            Assert.That(values[row * Resolution + Column(52.5f)], Is.EqualTo(0), "Cleared at the water");
            Assert.That(values[row * Resolution + Column(80f)], Is.GreaterThan(0), "Kept away from the water");
        }

        [Test]
        public void FilterLimitsTheCullRectOnlyWhenItNeedsTheWater()
        {
            LoogaTextureStamp stamp = AddShoreStamp(CreateArea(9f));
            Band(stamp.WaterFilter.EdgeBand, -StampWaterFilter.Unlimited, 5f, 0f);
            Rect limited = stamp.GetCullRect(HeightMapping.MicroVerseParity);
            Assert.That(limited.width, Is.LessThan(1000f), "Near water only");
            Band(stamp.WaterFilter.EdgeBand, 14f, StampWaterFilter.Unlimited, 10f);
            Rect open = stamp.GetCullRect(HeightMapping.MicroVerseParity);
            Assert.That(open.width, Is.GreaterThan(1e6f), "Far from water too");
        }

        private LoogaWaterArea CreateArea(float level)
        {
            _mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(20f, level, -50f), new Vector3(20f, level, 150f), new Vector3(50f, level, 150f),
                    new Vector3(50f, level, -50f)
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 }
            };
            _mesh.RecalculateBounds();
            var water = new GameObject("Water").AddComponent<MeshFilter>();
            water.transform.SetParent(_world.transform, false);
            water.sharedMesh = _mesh;
            LoogaMeshWaterSource source = water.gameObject.AddComponent<LoogaMeshWaterSource>();
            var area = new GameObject("Water Area").AddComponent<LoogaWaterArea>();
            area.transform.SetParent(_world.transform, false);
            area.Set(new[] { source }, 256, 32f);
            return area;
        }

        // A global base layer, then a global shore layer that only the water filter limits.
        private LoogaTextureStamp AddShoreStamp(LoogaWaterArea area)
        {
            AddTextureStamp(0);
            LoogaTextureStamp stamp = AddTextureStamp(1);
            stamp.WaterFilter.SetArea(area);
            return stamp;
        }

        private LoogaTextureStamp AddTextureStamp(int layer)
        {
            var stamp = new GameObject("Stamp " + layer).AddComponent<LoogaTextureStamp>();
            stamp.transform.SetParent(_world.transform, false);
            stamp.transform.position = new Vector3(50f, 0f, 50f);
            stamp.transform.localScale = new Vector3(100f, 1f, 100f);
            stamp.SetTexture(_layers[layer], 1f, false);
            stamp.Falloff.SetShape(FalloffShape.Global, Vector2.one, FalloffEasing.Linear);
            return stamp;
        }

        // A range from lower to upper with a smooth edge below the lower limit and above the upper limit.
        private static void Band(StampRangeFilter filter, float lower, float upper, float lowerSmoothness)
        {
            filter.Set(true, new Vector2(lower, upper), new Vector2(lowerSmoothness, 0f), 1f);
        }

        private byte[][] EvaluateTextures()
        {
            using StampAlphamaps alphamaps = _world.EvaluateTextures();
            var maps = new byte[alphamaps.GetAlphamaps(0).Count][];
            for (int i = 0; i < maps.Length; i++)
            {
                maps[i] = alphamaps.ReadAlphamap(0, i);
            }
            return maps;
        }

        // Shore layer weight (0 to 255) at x on the middle row.
        private static int Shore(byte[][] maps, float x)
        {
            int row = Resolution / 2;
            return maps[0][(row * Resolution + Column(x)) * 4 + 1];
        }

        private static int Column(float x)
        {
            return Mathf.Clamp(Mathf.FloorToInt(x / 100f * Resolution), 0, Resolution - 1);
        }
    }
}
