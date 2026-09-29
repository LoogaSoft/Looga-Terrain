using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class TerrainIntegrationTests
    {
        private Scene _scene;
        private TerrainData _data;
        private Material _material;
        private TerrainRenderer _renderer;
        private UnityEngine.Terrain _terrain;
        private TerrainData _guardA;
        private TerrainData _guardB;

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewPreviewScene();
            _data = new TerrainData { heightmapResolution = 33 };
            _guardA = new TerrainData();
            _guardB = new TerrainData();
            GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(_data);
            SceneManager.MoveGameObjectToScene(tile, _scene);
            _terrain = tile.GetComponent<UnityEngine.Terrain>();
            _material = new Material(Shader.Find("Looga/Terrain/Lit"));
            _renderer = tile.AddComponent<TerrainRenderer>();
            _renderer.Configure(AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/TerrainSelection.compute"), _material, 8);
            Assert.That(_renderer.IsReady, Is.True, _renderer.Diagnostic);
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(_scene);
            Object.DestroyImmediate(_data);
            Object.DestroyImmediate(_guardA);
            Object.DestroyImmediate(_guardB);
            Object.DestroyImmediate(_material);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void NativeVegetationOwnershipSurvivesRefreshAndFallback(bool visible)
        {
            _terrain.drawTreesAndFoliage = visible;
            _terrain.treeDistance = 173f;
            _terrain.detailObjectDistance = 83f;
            _terrain.detailObjectDensity = 0.4f;
            _renderer.RefreshSource();
            _renderer.SetIntegrationBlock(_guardA, "Fallback test");
            _renderer.SetIntegrationBlock(_guardA, null);
            _renderer.RefreshCompatibility();
            _renderer.enabled = false;
            Assert.That(_terrain.enabled, Is.True);
            Assert.That(_terrain.drawTreesAndFoliage, Is.EqualTo(visible));
            Assert.That(_terrain.treeDistance, Is.EqualTo(173f));
            Assert.That(_terrain.detailObjectDistance, Is.EqualTo(83f));
            Assert.That(_terrain.detailObjectDensity, Is.EqualTo(0.4f));
            Assert.That(_terrain.terrainData, Is.SameAs(_data));
            Assert.That(_terrain.GetComponent<TerrainCollider>().terrainData, Is.SameAs(_data));
        }

        [Test]
        public void MultipleFailuresRequireEveryGuardToClearBeforeRecovery()
        {
            _renderer.SetIntegrationBlock(_guardA, "Unsupported material");
            _renderer.SetIntegrationBlock(_guardB, "Authoring proxy");
            Assert.That(_renderer.IsReady, Is.False);
            Assert.That(_terrain.drawHeightmap, Is.True);
            Assert.That(_terrain.GetComponent<TerrainCollider>().terrainData, Is.SameAs(_data));
            _renderer.SetIntegrationBlock(_guardA, null);
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.False);
            Assert.That(_renderer.Diagnostic, Does.Contain("Authoring proxy"));
            _renderer.SetIntegrationBlock(_guardB, null);
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.True);
            Assert.That(_terrain.drawHeightmap, Is.False);
            _renderer.enabled = false;
            Assert.That(_terrain.drawHeightmap, Is.True);
        }

        [Test]
        public void ActiveExternalRendererKeepsVisibilityOwnership()
        {
            _terrain.drawHeightmap = false;
            _renderer.SetIntegrationBlock(_guardA, "Proxy active", true);
            Assert.That(_renderer.IsReady, Is.False);
            Assert.That(_terrain.drawHeightmap, Is.False);
            _terrain.drawHeightmap = true;
            _renderer.SetIntegrationBlock(_guardA, null);
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.True);
            Assert.That(_terrain.drawHeightmap, Is.False);
        }

        [Test]
        public void GuardRunsBeforeRecoveryAndCanRejectChangedConfiguration()
        {
            bool blocked = true;
            _renderer.ValidateIntegrations += () => _renderer.SetIntegrationBlock(_guardA, blocked ? "Changed features" : null);
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.False);
            blocked = false;
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.True);
            _terrain.drawHeightmap = true;
            _renderer.RefreshCompatibility();
            Assert.That(_terrain.drawHeightmap, Is.False);
        }

        [Test]
        public void SurfaceRefreshPreservesSourceAndAdvancesOnlySurfaceRevision()
        {
            int surface = _renderer.SurfaceRevision;
            int geometry = _renderer.GeometryRevision;
            _renderer.RefreshSurface();
            Assert.That(_renderer.SurfaceRevision, Is.GreaterThan(surface));
            Assert.That(_renderer.GeometryRevision, Is.EqualTo(geometry));
            Assert.That(_renderer.Source.terrainData, Is.SameAs(_data));
        }
    }
}
