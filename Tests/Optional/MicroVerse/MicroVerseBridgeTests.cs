using JBooth.MicroVerseCore;
using LoogaSoft.Terrain.Integrations;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class MicroVerseBridgeTests
    {
        private Scene _scene;
        private TerrainData _data;
        private Material _material;
        private TerrainRenderer _renderer;
        private MicroVerse _source;
        private MicroVerseTerrainBridge _bridge;
        private GameObject _root;

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewPreviewScene();
            _root = new GameObject("Isolated MicroVerse bridge test");
            SceneManager.MoveGameObjectToScene(_root, _scene);
            _root.SetActive(false);
            _data = new TerrainData { heightmapResolution = 33 };
            GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(_data);
            SceneManager.MoveGameObjectToScene(tile, _scene);
            tile.transform.SetParent(_root.transform);
            var terrain = tile.GetComponent<UnityEngine.Terrain>();
            terrain.allowAutoConnect = false;
            _material = new Material(Shader.Find("Looga/Terrain/Lit"));
            _renderer = tile.AddComponent<TerrainRenderer>();
            _renderer.Configure(AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/TerrainSelection.compute"), _material, 8);
            _source = _root.AddComponent<MicroVerse>();
            _source.enabled = false;
            _source.explicitTerrains = new[] { terrain };
            _bridge = _root.AddComponent<MicroVerseTerrainBridge>();
            _root.SetActive(true);
            _bridge.Configure(_source, null);
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.True, _renderer.Diagnostic);
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(_scene);
            Object.DestroyImmediate(_data);
            Object.DestroyImmediate(_material);
        }

        [Test]
        public void CompletionRefreshDoesNotClaimCpuSynchronization()
        {
            bool synchronized = _source.IsHeightSyncd;
            int revision = _renderer.SurfaceRevision;
            _bridge.RefreshRenderers();
            Assert.That(_renderer.SurfaceRevision, Is.GreaterThan(revision));
            Assert.That(_bridge.IsHeightSynchronized, Is.EqualTo(synchronized));
            Assert.That(_source.IsModifyingTerrain, Is.False);
            Assert.That(_renderer.Source.GetComponent<TerrainCollider>().terrainData, Is.SameAs(_data));
        }

#if UNITY_EDITOR && __MICROSPLAT__ && __MICROSPLAT_TESSELLATION__
        [Test]
        public void ProxyModeFallsBackThenRecoversWithoutDoubleNativeDrawing()
        {
            _source.proxyRenderMode = MicroVerse.ProxyRenderMode.AlwaysProxy;
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.False);
            Assert.That(_renderer.Source.drawHeightmap, Is.False);
            Assert.That(_renderer.Diagnostic, Does.Contain("MicroVerse proxy"));
            _source.proxyRenderMode = MicroVerse.ProxyRenderMode.AlwaysUnity;
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.True);
            Assert.That(_renderer.Source.drawHeightmap, Is.False);
        }

        [Test]
        public void InactiveProxyModeKeepsNativeFallback()
        {
            _source.proxyRenderMode = MicroVerse.ProxyRenderMode.ProxyWhileUpdating;
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.False);
            Assert.That(_renderer.Source.drawHeightmap, Is.True);
            _source.proxyRenderMode = MicroVerse.ProxyRenderMode.AlwaysUnity;
        }
#endif

        [Test]
        public void MembershipRefreshDiscoversNewTerrainRenderer()
        {
            var extraData = new TerrainData { heightmapResolution = 33 };
            GameObject extra = UnityEngine.Terrain.CreateTerrainGameObject(extraData);
            SceneManager.MoveGameObjectToScene(extra, _scene);
            extra.transform.SetParent(_root.transform);
            extra.transform.localPosition = Vector3.right * 1000;
            var terrain = extra.GetComponent<UnityEngine.Terrain>();
            terrain.allowAutoConnect = false;
            var renderer = extra.AddComponent<TerrainRenderer>();
            renderer.Configure(AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/TerrainSelection.compute"), _material, 8);
            try
            {
                _source.explicitTerrains = new[] { _renderer.Source, terrain };
                int revision = renderer.SurfaceRevision;
                _bridge.RefreshRenderers();
                Assert.That(renderer.SurfaceRevision, Is.GreaterThan(revision));
                _source.explicitTerrains = new[] { _renderer.Source };
                _bridge.RefreshRenderers();
                revision = renderer.SurfaceRevision;
                _bridge.RefreshRenderers();
                Assert.That(renderer.SurfaceRevision, Is.EqualTo(revision));
            }
            finally
            {
                Object.DestroyImmediate(extra);
                Object.DestroyImmediate(extraData);
            }
        }
    }
}
