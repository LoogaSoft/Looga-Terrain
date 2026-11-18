using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Stamps.Tests
{
    // Two 100 m tiles side by side along x, 129 samples, 200 m high: one 16-bit step is 400 m / 65535 = 6.1 mm.
    // A hill crosses the shared edge at x = 100 m.
    public sealed class SmoothStampTests
    {
        private const int Resolution = 129;
        private const float RealHeight = 400f;
        private const float Spacing = 100f / (Resolution - 1);
        private Scene _scene;
        private TerrainData[] _data;
        private UnityEngine.Terrain[] _terrains;
        private LoogaStampWorld _world;
        private Mesh _mesh;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Smooth stamps require compute shaders.");
            }
            _scene = EditorSceneManager.NewPreviewScene();
            _data = new TerrainData[2];
            _terrains = new UnityEngine.Terrain[2];
            for (int i = 0; i < 2; i++)
            {
                _data[i] = new TerrainData { heightmapResolution = Resolution, size = new Vector3(100f, 200f, 100f) };
                GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(_data[i]);
                SceneManager.MoveGameObjectToScene(tile, _scene);
                tile.transform.position = new Vector3(i * 100f, 0f, 0f);
                _terrains[i] = tile.GetComponent<UnityEngine.Terrain>();
            }
            _terrains[0].SetNeighbors(null, null, _terrains[1], null);
            _terrains[1].SetNeighbors(_terrains[0], null, null, null);
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
            Object.DestroyImmediate(_mesh);
        }

        [TestCase(HeightMapping.MicroVerseParity)]
        [TestCase(HeightMapping.WorldAligned)]
        public void BlurAcrossTheSeamMatchesTheJoinedReference(HeightMapping mapping)
        {
            SetMapping(mapping);
            AddHill();
            LoogaSmoothStamp smooth = AddSmooth(5f, 100f);
            smooth.enabled = false;
            float[,] joined = Joined(Evaluate());
            smooth.enabled = true;
            ushort[][] smoothed = Evaluate();
            float[,] expected = Blur(joined, 5f / Spacing, Mathf.CeilToInt(15f / Spacing));
            int largest = 0;
            for (int z = 0; z < Resolution; z++)
            {
                for (int x = 0; x < 2 * Resolution - 1; x++)
                {
                    int value = x < Resolution ? smoothed[0][z * Resolution + x] : smoothed[1][z * Resolution + x - 128];
                    largest = Mathf.Max(largest, Mathf.Abs(value - Mathf.RoundToInt(expected[z, x])));
                }
                Assert.That(smoothed[0][z * Resolution + 128], Is.EqualTo(smoothed[1][z * Resolution]), "Seam row " + z);
            }
            Assert.That(largest, Is.LessThanOrEqualTo(2), "Largest difference to the reference, in 16-bit steps");
            Assert.That(Mathf.Abs(joined[64, 128] - expected[64, 128]), Is.GreaterThan(50f), "The blur changes the hill");
        }

        [Test]
        public void MaxChangeLimitsTheChange()
        {
            AddHill();
            LoogaSmoothStamp smooth = AddSmooth(5f, 1f);
            smooth.enabled = false;
            ushort[][] before = Evaluate();
            smooth.enabled = true;
            ushort[][] after = Evaluate();
            int limit = Steps(1f), largest = 0;
            for (int tile = 0; tile < 2; tile++)
            {
                for (int i = 0; i < before[tile].Length; i++)
                {
                    largest = Mathf.Max(largest, Mathf.Abs(after[tile][i] - before[tile][i]));
                }
            }
            Assert.That(largest, Is.LessThanOrEqualTo(limit + 1));
            Assert.That(largest, Is.GreaterThanOrEqualTo(limit - 1), "The limit is reached on the hill flanks");
        }

        // Flat ground at 10 m, water at 9.9 m over x 20 to 50 m. With the rim rule the banks rise to 9.9 + 0.5 m.
        [TestCase(true, 10.4f)]
        [TestCase(false, 10f)]
        public void KeepAboveWaterRaisesTheBankToTheClearance(bool keep, float expected)
        {
            AddFlat(10f);
            LoogaWaterArea area = CreateWater(9.9f);
            LoogaSmoothStamp smooth = AddSmooth(2f, 3f);
            smooth.WaterFilter.SetArea(area);
            smooth.WaterFilter.LowGroundDistance.Set(true, new Vector2(0f, 30f), new Vector2(0f, 1f), 1f);
            smooth.SetWaterRim(keep, 4f, 0.5f);
            ushort[] tile = Evaluate()[0];
            int row = 64;
            Assert.That(tile[row * Resolution + Column(56f)], Is.EqualTo(Steps(expected)).Within(2), "Bank");
            Assert.That(tile[row * Resolution + Column(30f)], Is.EqualTo(Steps(10f)).Within(1), "Inside the water");
        }

        // Ground at 10 m, water at 10.4 m over x 20 to 50 m, a bank at 11 m from x 50 m. The blur lifts the bed next to
        // the bank. With the underwater rule the bed keeps its height.
        [TestCase(true)]
        [TestCase(false)]
        public void KeepUnderwaterKeepsTheBedNextToAHighBank(bool keep)
        {
            AddFlat(10f);
            var bank = new GameObject("Bank").AddComponent<LoogaHeightStamp>();
            bank.transform.SetParent(_world.transform, false);
            bank.transform.position = new Vector3(150f, 11f, 50f);
            bank.transform.localScale = new Vector3(200f, 10f, 400f);
            bank.SetHeight(HeightCombineMode.Override, null, 0f, 1f, Vector2.zero);
            bank.Falloff.SetShape(FalloffShape.Global, Vector2.one, FalloffEasing.Linear);
            LoogaWaterArea area = CreateWater(10.4f);
            LoogaSmoothStamp smooth = AddSmooth(2f, 3f);
            smooth.WaterFilter.SetArea(area);
            smooth.WaterFilter.LowGroundDistance.Set(true, new Vector2(-30f, 30f), new Vector2(0f, 1f), 1f);
            smooth.SetKeepUnderwater(keep);
            ushort[] tile = Evaluate()[0];
            int bed = tile[64 * Resolution + Column(48f)];
            if (keep)
            {
                Assert.That(bed, Is.EqualTo(Steps(10f)).Within(1), "Bed");
            }
            else
            {
                Assert.That(bed, Is.GreaterThan(Steps(10.1f)), "Bed");
            }
        }

        [Test]
        public void RegionUpdateOfOneTileReadsTheNeighbourPrefix()
        {
            AddHill();
            AddSmooth(5f, 100f);
            _world.RebuildHeights();
            float[,] full = _data[0].GetHeights(0, 0, Resolution, Resolution);
            _world.UpdateRegion(new Rect(10f, 10f, 5f, 5f));
            _world.SynchronizeHeights();
            float[,] region = _data[0].GetHeights(0, 0, Resolution, Resolution);
            for (int z = 0; z < Resolution; z++)
            {
                for (int x = 0; x < Resolution; x++)
                {
                    Assert.That(region[z, x], Is.EqualTo(full[z, x]), "Sample " + x + ", " + z);
                }
            }
        }

        // The second update writes only tile 0 and reads the ring that tile 1 stored in the first update.
        [Test]
        public void RegionUpdatesAfterAnEditMatchAFullRebuild()
        {
            AddHill();
            AddSmooth(5f, 100f);
            _world.RebuildHeights();
            Transform hill = _world.transform.Find("Hill");
            hill.position += new Vector3(20f, 0f, 0f);
            _world.UpdateRegion(_world.GrowForSmoothing(new Rect(60f, 10f, 100f, 80f)));
            _world.UpdateRegion(new Rect(10f, 10f, 5f, 5f));
            _world.SynchronizeHeights();
            float[][,] updated = { _data[0].GetHeights(0, 0, Resolution, Resolution),
                _data[1].GetHeights(0, 0, Resolution, Resolution) };
            _world.ReleaseResources();
            _world.RebuildHeights();
            _world.SynchronizeHeights();
            for (int tile = 0; tile < 2; tile++)
            {
                float[,] full = _data[tile].GetHeights(0, 0, Resolution, Resolution);
                for (int z = 0; z < Resolution; z++)
                {
                    for (int x = 0; x < Resolution; x++)
                    {
                        Assert.That(updated[tile][z, x], Is.EqualTo(full[z, x]), "Tile " + tile + ", " + x + ", " + z);
                    }
                }
            }
        }

        [Test]
        public void GrowForSmoothingAddsTheReachNearTheStamp()
        {
            LoogaSmoothStamp smooth = AddSmooth(5f, 3f);
            smooth.transform.position = new Vector3(50f, 0f, 50f);
            smooth.transform.localScale = new Vector3(20f, 10f, 20f);
            Rect near = _world.GrowForSmoothing(new Rect(55f, 55f, 2f, 2f));
            Assert.That(near.xMin, Is.EqualTo(40f).Within(0.01f), "Grown by three sigma");
            Rect far = _world.GrowForSmoothing(new Rect(150f, 50f, 2f, 2f));
            Assert.That(far.xMin, Is.EqualTo(150f), "Far from the stamp");
        }

        private void AddHill()
        {
            AddFlat(20f);
            // A flat stamp gives its bottom height: a 60 m plateau, 18 m wide, with flanks down to 36 m wide.
            var hill = new GameObject("Hill").AddComponent<LoogaHeightStamp>();
            hill.transform.SetParent(_world.transform, false);
            hill.transform.position = new Vector3(100f, 60f, 50f);
            hill.transform.localScale = new Vector3(60f, 10f, 60f);
            hill.SetHeight(HeightCombineMode.Max, null, 0f, 1f, Vector2.zero);
            hill.Falloff.SetShape(FalloffShape.Range, new Vector2(0.3f, 0.6f), FalloffEasing.Linear);
        }

        [TestCase(HeightMapping.MicroVerseParity)]
        [TestCase(HeightMapping.WorldAligned)]
        public void ReadSnapshotLeavesOutTheLaterModifiers(HeightMapping mapping)
        {
            SetMapping(mapping);
            AddHill();
            AddSmooth(5f, 100f);
            var snapshot = new GameObject("Snapshot").AddComponent<LoogaHeightSnapshot>();
            snapshot.transform.SetParent(_world.transform, false);
            var later = new GameObject("Later").AddComponent<LoogaHeightStamp>();
            later.transform.SetParent(_world.transform, false);
            later.transform.position = new Vector3(100f, 60f, 50f);
            later.transform.localScale = new Vector3(400f, 10f, 400f);
            later.SetHeight(HeightCombineMode.Override, null, 0f, 1f, Vector2.zero);
            later.Falloff.SetShape(FalloffShape.Global, Vector2.one, FalloffEasing.Linear);
            later.enabled = false;
            ushort[][] reference = Evaluate();
            later.enabled = true;

            using StampHeights heights = _world.ReadSnapshot(snapshot, new Rect(0f, 0f, 200f, 100f));
            Assert.AreEqual(2, heights.Terrains.Count);
            for (int i = 0; i < 2; i++)
            {
                int index = heights.Terrains[0] == _terrains[i] ? 0 : 1;
                CollectionAssert.AreEqual(reference[i], heights.ReadTile(index));
            }
            Assert.IsTrue(heights.TrySampleHeight(new Vector2(64 * Spacing, 64 * Spacing), out float height));
            Assert.AreEqual(reference[0][64 * Resolution + 64] / 32766f * 200f, height, 1e-3f);
        }

        private void AddFlat(float height)
        {
            var flat = new GameObject("Flat").AddComponent<LoogaHeightStamp>();
            flat.transform.SetParent(_world.transform, false);
            flat.transform.position = new Vector3(100f, height, 50f);
            flat.transform.localScale = new Vector3(400f, 10f, 400f);
            flat.SetHeight(HeightCombineMode.Override, null, 0f, 1f, Vector2.zero);
            flat.Falloff.SetShape(FalloffShape.Global, Vector2.one, FalloffEasing.Linear);
        }

        private LoogaSmoothStamp AddSmooth(float sigma, float maxChange)
        {
            var smooth = new GameObject("Smooth").AddComponent<LoogaSmoothStamp>();
            smooth.transform.SetParent(_world.transform, false);
            smooth.transform.position = new Vector3(100f, 0f, 50f);
            smooth.transform.localScale = new Vector3(400f, 10f, 400f);
            smooth.SetSmoothing(sigma, maxChange, 1f);
            smooth.Falloff.SetShape(FalloffShape.Global, Vector2.one, FalloffEasing.Linear);
            return smooth;
        }

        private LoogaWaterArea CreateWater(float level)
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

        private ushort[][] Evaluate()
        {
            using StampHeights heights = _world.EvaluateHeights();
            return new[] { heights.ReadTile(0), heights.ReadTile(1) };
        }

        // The two tiles as one grid of 129 x 257 samples. Column 128 is the shared edge.
        private static float[,] Joined(ushort[][] tiles)
        {
            var joined = new float[Resolution, 2 * Resolution - 1];
            for (int z = 0; z < Resolution; z++)
            {
                for (int x = 0; x < 2 * Resolution - 1; x++)
                {
                    joined[z, x] = x < Resolution ? tiles[0][z * Resolution + x] : tiles[1][z * Resolution + x - 128];
                }
            }
            return joined;
        }

        // Separable Gaussian with clamped edges, like the smooth stamp at the world edge.
        private static float[,] Blur(float[,] source, float sigma, int reach)
        {
            int depth = source.GetLength(0), width = source.GetLength(1);
            var weights = new float[2 * reach + 1];
            float total = 0f;
            for (int k = -reach; k <= reach; k++)
            {
                weights[k + reach] = Mathf.Exp(-(k * k) / (2f * sigma * sigma));
                total += weights[k + reach];
            }
            var temp = new float[depth, width];
            var result = new float[depth, width];
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    float sum = 0f;
                    for (int k = -reach; k <= reach; k++)
                    {
                        sum += source[z, Mathf.Clamp(x + k, 0, width - 1)] * weights[k + reach];
                    }
                    temp[z, x] = sum / total;
                }
            }
            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    float sum = 0f;
                    for (int k = -reach; k <= reach; k++)
                    {
                        sum += temp[Mathf.Clamp(z + k, 0, depth - 1), x] * weights[k + reach];
                    }
                    result[z, x] = sum / total;
                }
            }
            return result;
        }

        private void SetMapping(HeightMapping mapping)
        {
            var serialized = new UnityEditor.SerializedObject(_world);
            serialized.FindProperty("_mapping").enumValueIndex = (int)mapping;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static int Column(float x)
        {
            return Mathf.RoundToInt(x / Spacing);
        }

        private static int Steps(float metres)
        {
            return Mathf.RoundToInt(metres / RealHeight * 65535f);
        }
    }
}
