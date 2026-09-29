using LoogaSoft.Terrain.Editor;
using LoogaSoft.Terrain.Instances;
using LoogaSoft.Instancing;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class LoogaWorldSetupTests
    {
        private const string OutputFolder = "Assets/Tests/LoogaTerrain/WorldSetupGenerated";
        private Scene _scene;
        private TerrainData _terrainData;

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _terrainData = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 10, 32) };
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(OutputFolder);
            if (_terrainData) Object.DestroyImmediate(_terrainData);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [Test]
        public void ConfigureIsIdempotentAndRestoreRemovesOwnedComponents()
        {
            GameObject terrainObject = UnityEngine.Terrain.CreateTerrainGameObject(_terrainData);
            var cameraObject = new GameObject("Camera", typeof(Camera));
            var controllerObject = new GameObject("Looga World");
            var controller = controllerObject.AddComponent<LoogaWorldController>();
            controller.SetGeneratedAssetFolder(OutputFolder);
            controller.SetCameras(new[] { cameraObject.GetComponent<Camera>() });
            controller.ConfigureFeatures(true, true, false, false, false, true, false, false);

            LoogaWorldSetupUtility.Configure(controller);
            LoogaWorldSetupUtility.Configure(controller);

            Assert.That(terrainObject.GetComponents<TerrainRenderer>().Length, Is.EqualTo(1));
            Assert.That(terrainObject.GetComponents<TerrainVegetationProvider>().Length, Is.EqualTo(1));
            Assert.That(cameraObject.GetComponents<TerrainOcclusionCamera>().Length, Is.EqualTo(0));
            Assert.That(cameraObject.GetComponents<TerrainWaterCamera>().Length, Is.EqualTo(1));
            Assert.That(controller.OwnedComponents.Length, Is.EqualTo(3));

            LoogaWorldSetupUtility.RestoreNative(controller);

            Assert.That(terrainObject.GetComponent<TerrainRenderer>(), Is.Null);
            Assert.That(terrainObject.GetComponent<TerrainVegetationProvider>(), Is.Null);
            Assert.That(cameraObject.GetComponent<TerrainOcclusionCamera>(), Is.Null);
            Assert.That(cameraObject.GetComponent<TerrainWaterCamera>(), Is.Null);
            Assert.That(controller.OwnedComponents, Is.Empty);
        }

        [Test]
        public void RestorePreservesPreExistingComponents()
        {
            GameObject terrainObject = UnityEngine.Terrain.CreateTerrainGameObject(_terrainData);
            TerrainVegetationProvider existing = terrainObject.AddComponent<TerrainVegetationProvider>();
            var controllerObject = new GameObject("Looga World");
            var controller = controllerObject.AddComponent<LoogaWorldController>();
            controller.SetGeneratedAssetFolder(OutputFolder);
            controller.ConfigureFeatures(true, true, false, false, false, false, false, false);

            LoogaWorldSetupUtility.Configure(controller);
            LoogaWorldSetupUtility.RestoreNative(controller);

            Assert.That(terrainObject.GetComponent<TerrainVegetationProvider>(), Is.SameAs(existing));
        }

        [Test]
        public void ConfigureConnectsExplicitSceneRootsWithoutGlobalMeshDiscovery()
        {
            GameObject source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var controllerObject = new GameObject("Looga World");
            var controller = controllerObject.AddComponent<LoogaWorldController>();
            controller.SetGeneratedAssetFolder(OutputFolder);
            controller.ConfigureFeatures(false, false, false, false, false, false, false, false);
            controller.SetInstanceRoots(new[] { source }, maxDistance: 250);

            LoogaWorldSetupUtility.Configure(controller);

            SceneInstanceProvider provider = controller.GetComponent<SceneInstanceProvider>();
            Assert.That(provider, Is.Not.Null);
            Assert.That(provider.SourceStatus.RequestedSources, Is.EqualTo(1));

            controller.SetInstanceRoots(null);
            LoogaWorldSetupUtility.Configure(controller);
            Assert.That(controller.GetComponent<SceneInstanceProvider>(), Is.Null);

            LoogaWorldSetupUtility.RestoreNative(controller);
            Assert.That(controller.GetComponent<SceneInstanceProvider>(), Is.Null);
        }

        [Test]
        public void ConfigureCreatesSharedVirtualTextureSettingsAndReceiver()
        {
            GameObject terrainObject = UnityEngine.Terrain.CreateTerrainGameObject(_terrainData);
            var controllerObject = new GameObject("Looga World");
            var controller = controllerObject.AddComponent<LoogaWorldController>();
            controller.SetGeneratedAssetFolder(OutputFolder);
            controller.ConfigureFeatures(true, false, false, false, false, false, false, false);
            controller.ConfigureVirtualTexturing(true);

            LoogaWorldSetupUtility.Configure(controller);

            Assert.That(controller.VirtualTextureSettings, Is.Not.Null);
            Assert.That(terrainObject.GetComponent<TerrainVirtualTexture>(), Is.Not.Null);

            controller.ConfigureVirtualTexturing(false);
            LoogaWorldSetupUtility.Configure(controller);
            Assert.That(terrainObject.GetComponent<TerrainVirtualTexture>(), Is.Null);

            LoogaWorldSetupUtility.RestoreNative(controller);
            Assert.That(terrainObject.GetComponent<TerrainVirtualTexture>(), Is.Null);
        }
    }
}
