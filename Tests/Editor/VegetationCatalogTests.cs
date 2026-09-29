using System;
using System.Collections.Generic;
using System.Reflection;
using LoogaSoft.Instancing;
using LoogaSoft.Terrain.Editor;
using LoogaSoft.Terrain.Instances;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class VegetationCatalogTests
    {
        private Scene _scene;
        private readonly List<Object> _assets = new();

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewPreviewScene();
        }

        [TearDown]
        public void TearDown()
        {
            if (_scene.IsValid()) EditorSceneManager.ClosePreviewScene(_scene);
            foreach (Object asset in _assets)
                if (asset) Object.DestroyImmediate(asset);
            _assets.Clear();
        }

        [Test]
        public void BuildGroupsStableSourcesAndDoesNotCountDuplicateProviders()
        {
            Texture2D grass = CreateTexture("Grass");
            TerrainVegetationProvider first = CreateProvider("First", new[] { grass }, new[] { 2 });
            TerrainVegetationProvider second = CreateProvider("Second", new[] { grass }, new[] { 4 });

            List<VegetationCatalog.Entry> entries = VegetationCatalog.Build(new[] { first, first, second });

            Assert.AreEqual(1, entries.Count);
            Assert.AreSame(grass, entries[0].Source);
            Assert.AreEqual(VegetationKind.Detail, entries[0].Kind);
            Assert.AreEqual(2, entries[0].Usages.Count);
            Assert.IsTrue(VegetationCatalog.Recount(entries));
            Assert.AreEqual(6L, entries[0].SourceCount);
            Assert.AreEqual("Paint units", entries[0].CountLabel);
        }

        [Test]
        public void RecountCancellationKeepsAllPreviousCounts()
        {
            Texture2D amber = CreateTexture("Amber");
            Texture2D blue = CreateTexture("Blue");
            TerrainVegetationProvider provider = CreateProvider("Terrain", new[] { amber, blue }, new[] { 2, 3 });
            List<VegetationCatalog.Entry> entries = VegetationCatalog.Build(new[] { provider });
            Assert.IsTrue(VegetationCatalog.Recount(entries));
            Assert.AreEqual(2L, entries[0].SourceCount);
            Assert.AreEqual(3L, entries[1].SourceCount);

            int[,] changed = new int[16, 16];
            changed[0, 0] = 9;
            provider.GetComponent<UnityEngine.Terrain>().terrainData.SetDetailLayer(0, 0, 0, changed);
            bool completed = VegetationCatalog.Recount(entries, progress => progress >= 0.5f);

            Assert.IsFalse(completed);
            Assert.AreEqual(2L, entries[0].SourceCount);
            Assert.AreEqual(3L, entries[1].SourceCount);
        }

        [Test]
        public void BuildFollowsSourceReferenceWhenPrototypeSlotsAreReordered()
        {
            Texture2D amber = CreateTexture("Amber");
            Texture2D blue = CreateTexture("Blue");
            TerrainVegetationProvider provider = CreateProvider("Terrain", new[] { amber, blue }, new[] { 1, 2 });
            TerrainData data = provider.GetComponent<UnityEngine.Terrain>().terrainData;
            List<VegetationCatalog.Entry> before = VegetationCatalog.Build(new[] { provider });
            Assert.AreSame(amber, before[0].Source);
            Assert.AreEqual(0, before[0].Usages[0].PrototypeIndex);

            data.detailPrototypes = new[] { Detail(blue), Detail(amber) };
            List<VegetationCatalog.Entry> after = VegetationCatalog.Build(new[] { provider });

            Assert.AreSame(amber, after[0].Source);
            Assert.AreEqual(1, after[0].Usages[0].PrototypeIndex);
        }

        [Test]
        public void ControllerRestoresEarlierProvidersWhenOneProfileAssignmentFails()
        {
            Texture2D amber = CreateTexture("Amber");
            Texture2D blue = CreateTexture("Blue");
            TerrainVegetationProvider first = CreateProvider("First", new[] { amber }, new[] { 1 });
            TerrainVegetationProvider second = CreateProvider("Second", new[] { blue }, new[] { 1 });
            var controllerObject = new GameObject("World controller");
            SceneManager.MoveGameObjectToScene(controllerObject, _scene);
            LoogaWorldController controller = controllerObject.AddComponent<LoogaWorldController>();
            TerrainVegetationProvider[] providers = controller.GetVegetationProviders();
            Assert.AreEqual(2, providers.Length);
            Texture2D failingSource = providers[1] == first ? amber : blue;

            VegetationSettings previous = ScriptableObject.CreateInstance<VegetationSettings>();
            _assets.Add(previous);
            controller.SetVegetationSettings(previous);

            VegetationSettings settings = ScriptableObject.CreateInstance<VegetationSettings>();
            _assets.Add(settings);
            InstanceQualitySettings quality = InstanceQualitySettings.Default;
            quality.ViewDistance = 250;
            settings.SetOverride(VegetationKind.Detail, failingSource,
                VegetationQualityFields.ViewDistance, quality);
            VegetationSettings.OverrideEntry entry = settings.Overrides[0];
            quality.ViewDistance = float.NaN;
            typeof(VegetationSettings.OverrideEntry).GetField("_quality",
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(entry, quality);

            Assert.Throws<ArgumentOutOfRangeException>(() => controller.SetVegetationSettings(settings));
            Assert.AreSame(previous, controller.VegetationSettings);
            Assert.AreSame(previous, first.VegetationSettings);
            Assert.AreSame(previous, second.VegetationSettings);
        }

        [Test]
        public void ControllerAssignsOnlyProvidersInItsScene()
        {
            Texture2D grass = CreateTexture("Grass");
            TerrainVegetationProvider local = CreateProvider("Local", new[] { grass }, new[] { 1 });
            Scene otherScene = EditorSceneManager.NewPreviewScene();
            try
            {
                TerrainVegetationProvider remote = CreateProvider("Other scene", new[] { grass }, new[] { 1 }, otherScene);
                var controllerObject = new GameObject("World controller");
                SceneManager.MoveGameObjectToScene(controllerObject, _scene);
                LoogaWorldController controller = controllerObject.AddComponent<LoogaWorldController>();
                VegetationSettings settings = ScriptableObject.CreateInstance<VegetationSettings>();
                _assets.Add(settings);

                controller.SetVegetationSettings(settings);

                Assert.AreSame(settings, local.VegetationSettings);
                Assert.IsNull(remote.VegetationSettings);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(otherScene);
            }
        }

        private Texture2D CreateTexture(string name)
        {
            var texture = new Texture2D(2, 2) { name = name };
            _assets.Add(texture);
            return texture;
        }

        private TerrainVegetationProvider CreateProvider(string name, Texture2D[] sources, int[] paintUnits,
            Scene targetScene = default)
        {
            var data = new TerrainData { size = new Vector3(32, 10, 32) };
            _assets.Add(data);
            data.SetDetailResolution(16, 8);
            var prototypes = new DetailPrototype[sources.Length];
            for (int i = 0; i < sources.Length; i++) prototypes[i] = Detail(sources[i]);
            data.detailPrototypes = prototypes;
            for (int i = 0; i < paintUnits.Length; i++)
            {
                int[,] layer = new int[16, 16];
                layer[0, 0] = paintUnits[i];
                data.SetDetailLayer(0, 0, i, layer);
            }

            var owner = new GameObject(name);
            owner.SetActive(false);
            SceneManager.MoveGameObjectToScene(owner, targetScene.IsValid() ? targetScene : _scene);
            owner.AddComponent<UnityEngine.Terrain>().terrainData = data;
            TerrainVegetationProvider provider = owner.AddComponent<TerrainVegetationProvider>();
            owner.SetActive(true);
            return provider;
        }

        private static DetailPrototype Detail(Texture2D source)
        {
            return new DetailPrototype
            {
                prototypeTexture = source,
                renderMode = DetailRenderMode.GrassBillboard,
                minWidth = 1,
                maxWidth = 1,
                minHeight = 1,
                maxHeight = 1
            };
        }
    }
}
