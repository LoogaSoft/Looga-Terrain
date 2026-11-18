using JBooth.MicroSplat;
using LoogaSoft.Terrain.Integrations.Editor;
using LoogaSoft.Terrain.Stamps;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Tests
{
    // A stamp world with two texture stamps. The MicroSplat config must hold their layers sorted by name.
    public sealed class LoogaStampMicroSplatSyncTests
    {
        private Scene _scene;
        private TerrainLayer _sand;
        private TerrainLayer _grass;
        private TextureArrayConfig _config;
        private LoogaStampWorld _world;

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewPreviewScene();
            _sand = new TerrainLayer { name = "Sand" };
            _grass = new TerrainLayer { name = "Grass" };
            _config = ScriptableObject.CreateInstance<TextureArrayConfig>();
            var root = new GameObject("Stamp World");
            SceneManager.MoveGameObjectToScene(root, _scene);
            _world = root.AddComponent<LoogaStampWorld>();
            AddStamp(_sand);
            AddStamp(_grass);
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.ClosePreviewScene(_scene);
            Object.DestroyImmediate(_sand);
            Object.DestroyImmediate(_grass);
            Object.DestroyImmediate(_config);
        }

        [Test]
        public void LayersMatchOnlyInNameOrder()
        {
            SetConfig(_grass, _sand);
            Assert.That(LoogaStampMicroSplatSync.LayersMatch(_world, _config), Is.True, "Name order");
            SetConfig(_sand, _grass);
            Assert.That(LoogaStampMicroSplatSync.LayersMatch(_world, _config), Is.False, "Other order");
            SetConfig(_grass);
            Assert.That(LoogaStampMicroSplatSync.LayersMatch(_world, _config), Is.False, "Missing layer");
        }

        private void SetConfig(params TerrainLayer[] layers)
        {
            _config.sourceTextures.Clear();
            foreach (TerrainLayer layer in layers)
            {
                _config.sourceTextures.Add(new TextureArrayConfig.TextureEntry { terrainLayer = layer });
            }
        }

        private void AddStamp(TerrainLayer layer)
        {
            var stamp = new GameObject(layer.name).AddComponent<LoogaTextureStamp>();
            stamp.transform.SetParent(_world.transform, false);
            stamp.SetTexture(layer, 1f, false);
        }
    }
}
