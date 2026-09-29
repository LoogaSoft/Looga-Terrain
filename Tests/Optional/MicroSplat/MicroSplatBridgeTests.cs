using System;
using JBooth.MicroSplat;
using LoogaSoft.Terrain.Integrations;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class MicroSplatBridgeTests
    {
        private Scene _scene;
        private TerrainData _data;
        private Material _surface;
        private Material _native;
        private MicroSplatKeywords _keywords;
        private MicroSplatTerrain _source;
        private TerrainRenderer _renderer;
        private MicroSplatTerrainBridge _bridge;

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewPreviewScene();
            _data = new TerrainData { heightmapResolution = 33 };
            GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(_data);
            SceneManager.MoveGameObjectToScene(tile, _scene);
            tile.SetActive(false);
            _surface = new Material(Shader.Find("Looga/Terrain/Lit"));
            _native = new Material(_surface);
            _keywords = ScriptableObject.CreateInstance<MicroSplatKeywords>();
            _keywords.keywords.AddRange(new[] { "_MICROSPLAT", "_MICROTERRAIN" });
            _surface.SetOverrideTag(MicroSplatCompatibility.FeaturesTag, "_MICROSPLAT|_MICROTERRAIN");
            _surface.SetOverrideTag(MicroSplatCompatibility.ShaderTag, _native.shader.name);
            _source = tile.AddComponent<MicroSplatTerrain>();
            _source.enabled = false;
            _source.terrain = tile.GetComponent<UnityEngine.Terrain>();
            _source.matInstance = _native;
            _source.keywordSO = _keywords;
            _renderer = tile.AddComponent<TerrainRenderer>();
            _renderer.Configure(AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/TerrainSelection.compute"), _surface, 8);
            _bridge = tile.AddComponent<MicroSplatTerrainBridge>();
            tile.SetActive(true);
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.True, _renderer.Diagnostic);
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(_scene);
            Object.DestroyImmediate(_data);
            Object.DestroyImmediate(_surface);
            Object.DestroyImmediate(_native);
            Object.DestroyImmediate(_keywords);
        }

        [Test]
        public void UnsupportedFeaturesRestoreNativeAndRecoverWhenRemoved()
        {
            _keywords.keywords.Add("_TESSDISTANCE");
            _keywords.keywords.Add("_TESSONLYDISPLACE");
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.False);
            Assert.That(_source.terrain.drawHeightmap, Is.True);
            Assert.That(_bridge.Diagnostic, Does.Contain("_TESSDISTANCE").And.Contain("_TESSONLYDISPLACE"));
            _keywords.keywords.Remove("_TESSDISTANCE");
            _keywords.keywords.Remove("_TESSONLYDISPLACE");
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.True);
            Assert.That(_source.terrain.drawHeightmap, Is.False);
        }

        [TestCase("_GLOBALNORMALS")]
        [TestCase("_GEOMAP")]
        public void UnqualifiedGlobalGeometryFeaturesUseNativeRendering(string feature)
        {
            _keywords.keywords.Add(feature);
            _surface.SetOverrideTag(MicroSplatCompatibility.FeaturesTag, "_MICROSPLAT|_MICROTERRAIN|" + feature);
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.False);
            Assert.That(_source.terrain.drawHeightmap, Is.True);
            Assert.That(_bridge.Diagnostic, Does.Contain(feature).And.Contain("native rendering"));
        }

        [TestCase("_SNOW")]
        [TestCase("_DYNAMICFLOWS")]
        [TestCase("_WINDPARTICULATE")]
        [TestCase("_PARALLAX")]
        [TestCase("_ALPHAHOLETEXTURE")]
        public void LiveSurfaceFeaturesDoNotEnterTheStaticCache(string feature)
        {
            Assert.That(MicroSplatCompatibility.IsSupported(feature), Is.True);
            Assert.That(MicroSplatCompatibility.IsCacheSupported(feature), Is.False);
            _keywords.keywords.Add(feature);
            _surface.SetOverrideTag(MicroSplatCompatibility.FeaturesTag, "_MICROSPLAT|_MICROTERRAIN|" + feature);
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.True, _renderer.Diagnostic);
        }

        [Test]
        public void ChangedSupportedSchemaRequiresNewExport()
        {
            _keywords.keywords.Add("_TRIPLANAR");
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.False);
            Assert.That(_bridge.Diagnostic, Does.Contain("Re-export"));
            _surface.SetOverrideTag(MicroSplatCompatibility.FeaturesTag, "_MICROSPLAT|_MICROTERRAIN|_TRIPLANAR");
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.True);
        }

        [Test]
        public void MissingRequiredModuleTextureFallsBackToNative()
        {
            _keywords.keywords.Add("_GLOBALTINT");
            _surface.SetOverrideTag(MicroSplatCompatibility.FeaturesTag, "_MICROSPLAT|_MICROTERRAIN|_GLOBALTINT");
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.False);
            Assert.That(_source.terrain.drawHeightmap, Is.True);
            Assert.That(_bridge.Diagnostic, Does.Contain("_GlobalTintTex").And.Contain("native rendering"));
        }

        [Test]
        public void MissingExportMetadataFallsBackWithoutChangingSourceData()
        {
            _surface.SetOverrideTag(MicroSplatCompatibility.FeaturesTag, "");
            _renderer.RefreshCompatibility();
            Assert.That(_renderer.IsReady, Is.False);
            Assert.That(_bridge.Diagnostic, Does.Contain("metadata"));
            Assert.That(_source.terrain.terrainData, Is.SameAs(_data));
            Assert.That(_source.GetComponent<TerrainCollider>().terrainData, Is.SameAs(_data));
        }

        [Test]
        public void MaterialSynchronizationDoesNotEditExportedAsset()
        {
            Color original = _surface.GetColor("_BaseColor");
            _native.SetColor("_BaseColor", Color.red);
            int revision = _renderer.SurfaceRevision;
            int geometry = _renderer.GeometryRevision;
            _bridge.SynchronizeMaterial();
            Assert.That(_surface.GetColor("_BaseColor"), Is.EqualTo(original));
            Assert.That(_renderer.SurfaceRevision, Is.GreaterThan(revision));
            Assert.That(_renderer.GeometryRevision, Is.EqualTo(geometry));
            Assert.That(_renderer.IsReady, Is.True);
        }

        [Test]
        public void UnassignedDynamicStreamMapUsesBlackInsteadOfThrowing()
        {
            Shader shader = ShaderUtil.CreateShaderAsset("Shader \"Hidden/LoogaTests/NullStream\" { Properties { _DynamicStreamControl (\"Stream\", 2D) = \"black\" {} } SubShader { Pass {} } }");
            Shader previous = _native.shader;
            try
            {
                _native.shader = shader;
                _native.SetTexture("_DynamicStreamControl", null);
                var properties = new MaterialPropertyBlock();
                typeof(MicroSplatTerrainBridge).GetMethod("BindDynamic",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(_bridge, new object[] { properties });
                Assert.That(properties.GetTexture("_DynamicStreamControl"), Is.SameAs(Texture2D.blackTexture));
            }
            finally
            {
                _native.shader = previous;
                Object.DestroyImmediate(shader);
            }
        }

        [Test]
        public void SourceMaterialReplacementIsDetectedWithoutTerrainCallback()
        {
            var replacement = new Material(_native);
            try
            {
                int revision = _renderer.SurfaceRevision;
                _source.matInstance = replacement;
                _renderer.RefreshCompatibility();
                Assert.That(_renderer.SurfaceRevision, Is.GreaterThan(revision));
                Assert.That(_renderer.IsReady, Is.True);
                _source.matInstance = _native;
                _renderer.RefreshCompatibility();
            }
            finally
            {
                Object.DestroyImmediate(replacement);
            }
        }
    }
}
