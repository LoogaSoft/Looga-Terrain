using LoogaSoft.Terrain.Instances;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class TerrainVegetationAdapterTests
    {
        private Scene _scene;
        private TerrainData _data;
        private GameObject _prototype;
        private UnityEngine.Terrain _terrain;
        private TerrainVegetationProvider _provider;

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewPreviewScene();
            _data = new TerrainData { heightmapResolution = 33, size = new Vector3(64, 16, 64) };
            _prototype = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(_prototype, _scene);
            _prototype.SetActive(false);
            _data.treePrototypes = new[] { new TreePrototype { prefab = _prototype } };
            _data.treeInstances = new[]
            {
                new TreeInstance
                {
                    position = new Vector3(0.5f, 0, 0.5f), prototypeIndex = 0,
                    widthScale = 1, heightScale = 1, color = Color.white, lightmapColor = Color.white
                }
            };
            _data.SetDetailResolution(16, 8);
            _data.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);
            _data.detailPrototypes = new[]
            {
                new DetailPrototype
                {
                    prototype = _prototype, usePrototypeMesh = true, useInstancing = true,
                    renderMode = DetailRenderMode.VertexLit, minWidth = 1, maxWidth = 1,
                    minHeight = 1, maxHeight = 1, density = 1, healthyColor = Color.white, dryColor = Color.white
                }
            };
            var detail = new int[16, 16];
            detail[8, 8] = 8;
            detail[8, 9] = 8;
            detail[9, 8] = 8;
            detail[9, 9] = 8;
            _data.SetDetailLayer(0, 0, 0, detail);
            GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(_data);
            SceneManager.MoveGameObjectToScene(tile, _scene);
            _terrain = tile.GetComponent<UnityEngine.Terrain>();
            _terrain.detailObjectDensity = 1;
            _provider = tile.AddComponent<TerrainVegetationProvider>();
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(_scene);
            Object.DestroyImmediate(_data);
        }

        [Test]
        public void CompleteTerrainPopulationTransfersAndDisableRestoresNativeOwnership()
        {
            _terrain.drawTreesAndFoliage = true;
            Assert.IsTrue(_provider.ProcessChanges(), _provider.Diagnostic);
            Assert.IsFalse(_terrain.drawTreesAndFoliage);
            Assert.AreEqual(1, _provider.SourceStatus.LoogaOwnedSources);
            Assert.GreaterOrEqual(_provider.InstanceCount, 2, "Tree and detail adapters must contribute to the complete population.");
            Assert.IsTrue(_provider.SourceStatus.HasCompleteCoverage);
            _provider.enabled = false;
            Assert.IsTrue(_terrain.drawTreesAndFoliage);
            Assert.AreEqual(1, _provider.SourceStatus.NativeFallbackSources);
        }

        [Test]
        public void UnsupportedTerrainPrototypeNeverHidesNativePopulation()
        {
            _data.treePrototypes = new[] { new TreePrototype { prefab = _prototype, bendFactor = 1 } };
            _provider.InvalidateAll();
            Assert.IsFalse(_provider.ProcessChanges());
            Assert.IsTrue(_terrain.drawTreesAndFoliage);
            Assert.AreEqual(0, _provider.SourceStatus.LoogaOwnedSources);
            Assert.AreEqual(1, _provider.SourceStatus.NativeFallbackSources);
            StringAssert.Contains("Native vegetation remains enabled", _provider.Diagnostic);
        }
    }
}
