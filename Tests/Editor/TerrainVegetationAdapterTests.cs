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

        [Test]
        public void HeightWriteWithoutTreesKeepsLoogaAndLoadsTheChangedPatchesAfterTheSynchronization()
        {
            _data.treeInstances = new TreeInstance[0];
            // 8 x 8 detail patches, so a patch update and a full rebuild read a different number of patches.
            _data.heightmapResolution = 257;
            _data.size = new Vector3(64, 16, 64);
            _data.SetDetailResolution(64, 8);
            var detail = new int[64, 64];
            detail[40, 40] = 8;
            _data.SetDetailLayer(0, 0, 0, detail);
            _terrain.drawTreesAndFoliage = true;
            Assert.IsTrue(_provider.ProcessChanges(), _provider.Diagnostic);
            Assert.AreEqual(64, _provider.LastPatchReads, "A full rebuild reads all patches");
            int count = _provider.InstanceCount;
            WriteHeights(new RectInt(0, 0, 8, 8));
            Assert.IsTrue(_provider.ProcessChanges(), _provider.Diagnostic);
            Assert.IsFalse(_terrain.drawTreesAndFoliage, "No native fallback while the heights synchronize");
            Assert.AreEqual(1, _provider.SourceStatus.LoogaOwnedSources);
            Assert.IsTrue(_provider.SourceStatus.IsWaiting);
            Assert.AreEqual(0, _provider.LastPatchReads, "Patches wait for the CPU heights");
            Assert.AreEqual(count, _provider.InstanceCount);
            _data.SyncHeightmap();
            Assert.IsTrue(_provider.ProcessChanges(), _provider.Diagnostic);
            Assert.IsFalse(_terrain.drawTreesAndFoliage);
            Assert.IsFalse(_provider.SourceStatus.IsWaiting);
            Assert.That(_provider.LastPatchReads, Is.InRange(1, 8), "Only the patches near the change load again");
            Assert.AreEqual(count, _provider.InstanceCount);
        }

        [Test]
        public void HeightWriteWithTreesWaitsOnNativeVegetation()
        {
            Assert.IsTrue(_provider.ProcessChanges(), _provider.Diagnostic);
            WriteHeights(new RectInt(0, 0, 8, 8));
            Assert.IsFalse(_provider.ProcessChanges());
            Assert.IsTrue(_terrain.drawTreesAndFoliage, "Trees are read again in full after the synchronization");
            _data.SyncHeightmap();
            Assert.IsTrue(_provider.ProcessChanges(), _provider.Diagnostic);
            Assert.IsFalse(_terrain.drawTreesAndFoliage);
        }

        // Writes a constant height into a heightmap area without a CPU synchronization, as GPU terrain tools do.
        private void WriteHeights(RectInt area)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture target = RenderTexture.GetTemporary(area.width, area.height, 0,
                UnityEngine.Terrain.heightmapRenderTextureFormat);
            try
            {
                RenderTexture.active = target;
                GL.Clear(false, true, new Color(0.25f, 0f, 0f, 0f));
                _data.CopyActiveRenderTextureToHeightmap(new RectInt(0, 0, area.width, area.height), area.position,
                    TerrainHeightmapSyncControl.None);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
            }
        }
    }
}
