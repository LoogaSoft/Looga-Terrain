using System;
using LoogaSoft.Instancing;
using LoogaSoft.Terrain.Instances;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class TerrainVegetationPreviewTests
    {
        private Scene _scene;
        private TerrainData _data;
        private UnityEngine.Terrain _terrain;
        private TerrainVegetationProvider _provider;
        private Camera _camera;

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewPreviewScene();
            _data = new TerrainData { heightmapResolution = 33, size = new Vector3(64, 16, 64) };
            _data.SetDetailResolution(16, 8);
            GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(_data);
            SceneManager.MoveGameObjectToScene(tile, _scene);
            _terrain = tile.GetComponent<UnityEngine.Terrain>();
            _provider = tile.AddComponent<TerrainVegetationProvider>();
            var observer = new GameObject("Preview observer", typeof(Camera));
            SceneManager.MoveGameObjectToScene(observer, _scene);
            _camera = observer.GetComponent<Camera>();
            _camera.enabled = false;
            _camera.transform.position = new Vector3(32, 3, 32);
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(_scene);
            Object.DestroyImmediate(_data);
        }

        [Test]
        public void PreviewRestoresNativeVisibilityWithoutSerializingObserverChanges()
        {
            string before = EditorJsonUtility.ToJson(_provider);
            _provider.BeginEditorPreview(new[] { _camera.transform });
            Assert.IsTrue(_provider.ProcessChanges(), _provider.Diagnostic);
            Assert.IsTrue(_provider.EditorPreviewActive);
            Assert.IsFalse(_terrain.drawTreesAndFoliage);
            _provider.EndEditorPreview();
            Assert.IsFalse(_provider.EditorPreviewActive);
            Assert.IsFalse(_provider.IsRendering);
            Assert.IsTrue(_terrain.drawTreesAndFoliage);
            Assert.AreEqual(before, EditorJsonUtility.ToJson(_provider));
        }

        [Test]
        public void AuthoringBlockRestoresNativeAndResumesOnlyAfterBlockClears()
        {
            _provider.BeginEditorPreview(new[] { _camera.transform });
            Assert.IsTrue(_provider.ProcessChanges(), _provider.Diagnostic);
            _provider.SetIntegrationBlock(_camera, "Terrain authoring in progress");
            Assert.IsFalse(_provider.ProcessChanges());
            Assert.IsTrue(_terrain.drawTreesAndFoliage);
            _provider.SetIntegrationBlock(_camera, null);
            Assert.IsTrue(_provider.ProcessChanges(), _provider.Diagnostic);
            _provider.EndEditorPreview();
            Assert.IsTrue(_terrain.drawTreesAndFoliage);
        }

        [Test]
        public void PreviewUsesAuthoredQualityAndRestoresDerivedRuntimeScale()
        {
            InstanceQualitySettings details = InstanceQualitySettings.Default;
            details.Decorative = true;
            details.Density = 1;
            _provider.ConfigureQuality(InstanceQualitySettings.Default, details, InstanceVisibilityMode.Direct);
            _provider.ApplyAdaptiveQuality(new AdaptiveQualityState(0.5f, 20, 10));
            float scaledDensity = _provider.GetCurrentQuality(VegetationKind.Detail, null).Density;
            Assert.Less(scaledDensity, 1);
            _provider.BeginEditorPreview(new[] { _camera.transform });
            Assert.AreEqual(1, _provider.GetCurrentQuality(VegetationKind.Detail, null).Density);
            _provider.EndEditorPreview();
            Assert.AreEqual(scaledDensity, _provider.GetCurrentQuality(VegetationKind.Detail, null).Density);
        }

        [Test]
        public void DisablingProviderClearsTemporaryObserverOwnership()
        {
            _provider.BeginEditorPreview(new[] { _camera.transform });
            Assert.IsTrue(_provider.ProcessChanges(), _provider.Diagnostic);
            _provider.enabled = false;
            Assert.IsFalse(_provider.EditorPreviewActive);
            Assert.IsTrue(_terrain.drawTreesAndFoliage);
        }

        [Test]
        public void InvalidObserverDoesNotAcquireNativeOwnership()
        {
            Assert.Throws<ArgumentException>(() => _provider.BeginEditorPreview(Array.Empty<Transform>()));
            Assert.Throws<ArgumentException>(() => _provider.BeginEditorPreview(new[] { _terrain.transform }));
            Assert.IsTrue(_terrain.drawTreesAndFoliage);
            Assert.IsFalse(_provider.EditorPreviewActive);
        }

        [Test]
        public void PreviewDoesNotTakeAnotherRenderersNativeOwnership()
        {
            _terrain.drawTreesAndFoliage = false;
            _provider.BeginEditorPreview(new[] { _camera.transform });
            Assert.IsFalse(_provider.ProcessChanges());
            _provider.EndEditorPreview();
            Assert.IsFalse(_terrain.drawTreesAndFoliage);
        }
    }
}
