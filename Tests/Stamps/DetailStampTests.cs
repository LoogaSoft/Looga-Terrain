using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Stamps.Tests
{
    // One flat 1 km tile with two detail layers in coverage mode. Global stamps give the same value on every texel. The
    // expected values are the MicroVerse values of MEASUREMENTS.md 10.4: 0.878 gives 224 and 0.3 gives 76.
    public sealed class DetailStampTests
    {
        private const int DetailResolution = 64;

        private Scene _scene;
        private TerrainData _data;
        private UnityEngine.Terrain _terrain;
        private Texture2D _grass;
        private LoogaStampWorld _world;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("Placement stamps require compute shaders.");
            }
            _scene = EditorSceneManager.NewPreviewScene();
            _data = new TerrainData { heightmapResolution = 33, alphamapResolution = 64 };
            _data.size = new Vector3(1000f, 200f, 1000f);
            _data.SetDetailResolution(DetailResolution, 16);
            _data.SetDetailScatterMode(DetailScatterMode.CoverageMode);
            _grass = new Texture2D(4, 4);
            _data.detailPrototypes = new[] { CreatePrototype(), CreatePrototype() };
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
        }

        [TestCase(0.878f, 224)]
        [TestCase(0.3f, 76)]
        [TestCase(1f, 255)]
        public void GlobalStampWritesTheCoverageValue(float weight, int expected)
        {
            AddDetailStamp(0, weight);
            AssertLayer(_world.EvaluatePlacements(), 0, expected);
        }

        [Test]
        public void StampsOfOneLayerKeepTheLargerValue()
        {
            AddDetailStamp(0, 0.878f);
            AddDetailStamp(0, 0.3f);
            StampPlacements placements = _world.EvaluatePlacements();
            AssertLayer(placements, 0, 224);
            Assert.That(placements.TryGetDetailLayer(_terrain, 1, out _), Is.False, "No stamp paints layer 1");
        }

        // The clear map stores the weight sum in 8 bits: 0.6 is 153 / 255, so 1 - 0.6 gives 102.
        [TestCase(true, true, 102)]
        [TestCase(false, true, 255)]
        [TestCase(true, false, 255)]
        public void ClearAfterTheStampLowersTheWeight(bool clearAfter, bool clearDetails, int expected)
        {
            if (!clearAfter)
            {
                AddClear(0.6f, clearDetails);
            }
            AddDetailStamp(0, 1f);
            if (clearAfter)
            {
                AddClear(0.6f, clearDetails);
            }
            AssertLayer(_world.EvaluatePlacements(), 0, expected);
        }

        // Layer 0 at 255 makes room for a layer 1 stamp at 0.5 (127 / 255) with displacement 0.6:
        // 255 x (1 - 0.6 x 127 / 255) = 178.8, stored as 179. A layer that the stamp does not list, or a later stamp,
        // keeps its value.
        [TestCase(true, false, 179)]
        [TestCase(false, false, 255)]
        [TestCase(true, true, 255)]
        public void DisplacingStampLowersTheEarlierLayers(bool listed, bool laterStamp, int expected)
        {
            if (!laterStamp)
            {
                AddDetailStamp(0, 1f);
            }
            LoogaDetailStamp stamp = AddDetailStamp(1, 0.5f);
            stamp.SetDisplacement(0.6f, listed ? new[] { 0 } : new int[0]);
            if (laterStamp)
            {
                AddDetailStamp(0, 1f);
            }
            StampPlacements placements = _world.EvaluatePlacements();
            AssertLayer(placements, 0, expected);
            AssertLayer(placements, 1, 127);
        }

        [Test]
        public void WeightOutsideTheRangeGivesZero()
        {
            LoogaDetailStamp stamp = AddDetailStamp(0, 0.5f);
            stamp.SetDetail(0, 0.5f, new Vector2(0.6f, 1f), false, true);
            AssertLayer(_world.EvaluatePlacements(), 0, 0);
        }

        // Worley noise with amplitude 1 and balance 1 gives a weight from 0 to 1 that changes across the tile.
        [Test]
        public void WorleyWeightNoiseVariesTheWeight()
        {
            LoogaDetailStamp stamp = AddDetailStamp(0, 1f);
            stamp.WeightNoise.Set(StampNoiseType.Worley, StampNoiseSpace.World, 20f, 1f, 0f, 1f);
            Assert.That(_world.EvaluatePlacements().TryGetDetailLayer(_terrain, 0, out byte[] values), Is.True);
            int minimum = 255, maximum = 0;
            foreach (byte value in values)
            {
                minimum = Mathf.Min(minimum, value);
                maximum = Mathf.Max(maximum, value);
            }
            Assert.That(maximum - minimum, Is.GreaterThan(100));
        }

        [Test]
        public void WriteReplacesOnlyThePaintedLayers()
        {
            var other = new int[DetailResolution, DetailResolution];
            other[3, 5] = 9;
            _data.SetDetailLayer(0, 0, 1, other);
            AddDetailStamp(0, 0.3f);
            _world.RebuildPlacements();
            int[,] painted = _data.GetDetailLayer(0, 0, DetailResolution, DetailResolution, 0);
            int[,] kept = _data.GetDetailLayer(0, 0, DetailResolution, DetailResolution, 1);
            Assert.That(painted[10, 20], Is.EqualTo(76), "Painted layer");
            Assert.That(kept[3, 5], Is.EqualTo(9), "Other layer");
        }

        private DetailPrototype CreatePrototype()
        {
            return new DetailPrototype
            {
                prototypeTexture = _grass,
                renderMode = DetailRenderMode.Grass,
                usePrototypeMesh = false
            };
        }

        private LoogaDetailStamp AddDetailStamp(int layer, float weight)
        {
            var stamp = new GameObject("Detail Stamp").AddComponent<LoogaDetailStamp>();
            stamp.transform.SetParent(_world.transform, false);
            stamp.transform.localScale = new Vector3(1000f, 100f, 1000f);
            stamp.Falloff.SetShape(FalloffShape.Global, Vector2.one, FalloffEasing.Linear);
            stamp.SetDetail(layer, weight, new Vector2(0f, 999999f), false, true);
            return stamp;
        }

        private void AddClear(float weight, bool clearDetails)
        {
            var clear = new GameObject("Clear").AddComponent<LoogaClearStamp>();
            clear.transform.SetParent(_world.transform, false);
            clear.transform.localScale = new Vector3(1000f, 100f, 1000f);
            clear.Falloff.SetShape(FalloffShape.Global, Vector2.one, FalloffEasing.Linear);
            clear.SetClear(true, clearDetails, true, weight, false);
        }

        private void AssertLayer(StampPlacements placements, int layer, int expected)
        {
            Assert.That(placements.TryGetDetailLayer(_terrain, layer, out byte[] values), Is.True, "Layer evaluated");
            Assert.That(values.Length, Is.EqualTo(DetailResolution * DetailResolution));
            foreach (byte value in values)
            {
                Assert.That(value, Is.EqualTo(expected));
            }
        }
    }
}
