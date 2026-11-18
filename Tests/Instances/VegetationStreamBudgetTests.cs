using LoogaSoft.Instancing;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Instances.Tests
{
    public sealed class VegetationStreamBudgetTests
    {
        // A reservation that no test stream reaches.
        private const long LargeReservation = 1L << 40;

        private Scene _scene;
        private TerrainData _data;
        private UnityEngine.Terrain _terrain;
        private Transform _first;
        private Transform _second;

        [SetUp]
        public void SetUp()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Assert.Ignore("GPU-resident details require compute shaders.");
            }
            _scene = EditorSceneManager.NewPreviewScene();
            _data = new TerrainData { heightmapResolution = 33, size = new Vector3(64, 16, 64) };
            _data.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);
            // Two by two patches of 32 m.
            _data.SetDetailResolution(16, 8);
            GameObject prototype = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(prototype, _scene);
            prototype.SetActive(false);
            _data.detailPrototypes = new[]
            {
                new DetailPrototype
                {
                    prototype = prototype, usePrototypeMesh = true, useInstancing = true,
                    renderMode = DetailRenderMode.VertexLit, minWidth = 0.2f, maxWidth = 0.2f,
                    minHeight = 0.2f, maxHeight = 0.2f, density = 1
                }
            };
            var density = new int[16, 16];
            for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) density[z, x] = 4;
            _data.SetDetailLayer(0, 0, 0, density);
            GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(_data);
            SceneManager.MoveGameObjectToScene(tile, _scene);
            _terrain = tile.GetComponent<UnityEngine.Terrain>();
            _terrain.detailObjectDensity = 1;
            _first = CreateObserver(new Vector3(16, 0, 16));
            _second = CreateObserver(new Vector3(48, 0, 48));
        }

        [TearDown]
        public void TearDown()
        {
            if (_scene.IsValid()) EditorSceneManager.ClosePreviewScene(_scene);
            if (_data) Object.DestroyImmediate(_data);
        }

        // Before the fix, a required cell over the instance limit threw, and the provider gave the whole terrain back
        // to native rendering. The GPU path also kept the pages and ranges of the rejected cell.
        [Test]
        public void RequiredCellsOverTheInstanceLimitStayMissingWithoutLeaks()
        {
            int first = LoadedCount(int.MaxValue, _first);
            int second = LoadedCount(int.MaxValue, _second);
            Assert.Greater(first, 0);
            Assert.Greater(second, 0);
            // Each cell fits alone. Both cells do not fit.
            int limit = Mathf.Max(first, second) + Mathf.Min(first, second) / 2;

            using var stream = new TerrainVegetationStream(_terrain, null, limit, 64, null, "budget-test", true);
            var both = new[] { _first, _second };
            Assert.DoesNotThrow(() => Update(stream, both));
            Assert.IsTrue(stream.BudgetLimited);
            Assert.IsFalse(stream.IsReady);
            int resident = stream.ResidentCount;
            Assert.That(resident, Is.EqualTo(first).Or.EqualTo(second));
            string storage = stream.DescribeDetailStorage();
            for (int i = 0; i < 4; i++)
            {
                Update(stream, both);
            }
            Assert.AreEqual(resident, stream.ResidentCount, "Repeated budget-limited updates must not allocate.");
            Assert.AreEqual(storage, stream.DescribeDetailStorage());

            // The first observer leaves. Its cell is no longer required, so the second cell can load.
            Update(stream, new[] { _second });
            Assert.IsFalse(stream.BudgetLimited);
            Assert.IsTrue(stream.IsReady);
            Assert.AreEqual(second, stream.ResidentCount);
        }

        // Before the fix, required cells over the GPU reservation threw, and the provider gave the terrain back to native
        // rendering. Each cell has 256 instances, so the second cell grows the populations to the next power of two.
        [Test]
        public void RequiredCellsOverTheGpuReservationStayMissingWithoutLeaks()
        {
            long first = ProjectedBytes(LargeReservation, _first);
            long both = ProjectedBytes(LargeReservation, _first, _second);
            if (both <= first)
            {
                Assert.Inconclusive("The second cell does not grow the GPU storage in this setup.");
            }
            long reservation = (first + both) / 2;

            using var stream = new TerrainVegetationStream(_terrain, null, int.MaxValue, 64, null, "gpu-test", true);
            var observers = new[] { _first, _second };
            Assert.DoesNotThrow(() => Update(stream, observers, reservation));
            Assert.IsTrue(stream.BudgetLimited);
            Assert.IsFalse(stream.IsReady);
            Assert.IsTrue(stream.IsRendering, "A budget-limited GPU stream must draw its loaded cells, not native details.");
            Assert.LessOrEqual(stream.ProjectedGpuBytes, reservation);
            int resident = stream.ResidentCount;
            Assert.Greater(resident, 0);
            string storage = stream.DescribeDetailStorage();
            for (int i = 0; i < 4; i++)
            {
                Assert.DoesNotThrow(() => Update(stream, observers, reservation));
            }
            Assert.AreEqual(resident, stream.ResidentCount, "Repeated reservation-limited updates must not allocate.");
            Assert.AreEqual(storage, stream.DescribeDetailStorage());

            // A larger reservation lets the missing cell load.
            Update(stream, observers, LargeReservation);
            Assert.IsFalse(stream.BudgetLimited);
            Assert.IsTrue(stream.IsReady);
        }

        // Details are culled by 3D camera distance, so an observer high above the terrain requires no detail cells.
        [Test]
        public void ObserverAboveTheDetailRangeRequiresNoDetailCells()
        {
            using var stream = new TerrainVegetationStream(_terrain, null, int.MaxValue, 64, null, "height-test", true);
            _first.position = new Vector3(16, 100, 16);
            Update(stream, new[] { _first });
            Assert.AreEqual(0, stream.RequiredCells);
            Assert.AreEqual(0, stream.ResidentCount);
            _first.position = new Vector3(16, 0, 16);
            Update(stream, new[] { _first });
            Assert.AreEqual(1, stream.RequiredCells);
            Assert.Greater(stream.ResidentCount, 0);
        }

        private int LoadedCount(int limit, Transform observer)
        {
            using var stream = new TerrainVegetationStream(_terrain, null, limit, 64, null, "budget-reference", true);
            Update(stream, new[] { observer });
            Assert.IsTrue(stream.IsReady);
            return stream.ResidentCount;
        }

        private long ProjectedBytes(long reservation, params Transform[] observers)
        {
            using var stream = new TerrainVegetationStream(_terrain, null, int.MaxValue, 64, null, "gpu-reference", true);
            Update(stream, observers, reservation);
            Assert.IsTrue(stream.IsReady);
            return stream.ProjectedGpuBytes;
        }

        private static void Update(TerrainVegetationStream stream, Transform[] observers)
        {
            Update(stream, observers, VegetationStreamingBudget.Default.ResidentGpuBytes);
        }

        private static void Update(TerrainVegetationStream stream, Transform[] observers, long reservation)
        {
            VegetationStreamingBudget budget = VegetationStreamingBudget.Default;
            budget.CpuMilliseconds = 1000;
            budget.ResidentGpuBytes = reservation;
            stream.Update(observers, 0, 64, 0, 0, budget);
        }

        private Transform CreateObserver(Vector3 position)
        {
            var observer = new GameObject("Observer").transform;
            SceneManager.MoveGameObjectToScene(observer.gameObject, _scene);
            observer.position = position;
            return observer;
        }
    }
}
