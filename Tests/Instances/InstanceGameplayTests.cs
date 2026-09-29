using LoogaSoft.Instancing;
using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Instances.Tests
{
    public sealed class InstanceGameplayTests
    {
        private Scene _scene;
        private InstanceContainer _container;
        private GameObject _prototype;
        private InstanceEditJournal _journal;

        [SetUp]
        public void Setup()
        {
            _scene = EditorSceneManager.NewPreviewScene();
            _prototype = New("Collider source");
            _prototype.AddComponent<BoxCollider>();
            _container = New("Authored placements").AddComponent<InstanceContainer>();
            _container.enabled = false;
            _container.Configure(_prototype, new[] { At("a", 0), At("b", 20) });
            _journal = new InstanceEditJournal();
            _container.BindEdits("tile/revision/container", _journal);
        }

        [TearDown]
        public void Cleanup() => EditorSceneManager.ClosePreviewScene(_scene);

        private GameObject New(string name)
        {
            var result = new GameObject(name);
            SceneManager.MoveGameObjectToScene(result, _scene);
            return result;
        }

        private static InstanceContainer.Placement At(string id, float x) =>
            new() { Id = id, Position = Vector3.right * x, Scale = Vector3.one };

        [Test]
        public void EditsPreserveAuthoredDataAndSurviveSourceReplacement()
        {
            _journal.Remove(_container.SourceId, "a");
            _journal.Override(_container.SourceId, At("b", 30));
            Assert.AreEqual(2, _container.CopyPlacements().Length);
            Assert.AreEqual(1, _container.CopyResolvedPlacements().Length);
            Assert.AreEqual(30, _container.CopyResolvedPlacements()[0].Position.x);
            _container.Configure(_prototype, new[] { At("a", 0), At("b", 20) });
            Assert.AreEqual(1, _container.CopyResolvedPlacements().Length);
            Assert.IsTrue(_journal.Restore(_container.SourceId, "a"));
            Assert.AreEqual(2, _container.CopyResolvedPlacements().Length);
        }

        [Test]
        public void SavedEditsRoundTripAndInvalidImportIsAtomic()
        {
            _journal.Remove("source", "a");
            _journal.Override("source", At("b", 30));
            var copy = JsonUtility.FromJson<InstanceEditJournal>(JsonUtility.ToJson(_journal));
            Assert.AreEqual(2, copy.Count);
            Assert.IsFalse(copy.Resolve("source", At("a", 0), out _));
            Assert.IsTrue(copy.Resolve("another-source", At("a", 0), out _));
            var entries = copy.Export();
            Assert.Throws<ArgumentException>(() => copy.Import(new[] { entries[0], entries[0] }));
            Assert.AreEqual(2, copy.Count);
            Assert.Throws<ArgumentException>(() => copy.Override("source", At(null, 0)));
        }

        [Test]
        public void DeserializeOverwriteDiscardsThePreviousEditLookup()
        {
            _journal.Remove("source", "old");
            Assert.IsTrue(_journal.TryGet("source", "old", out _));
            var replacement = new InstanceEditJournal();
            replacement.Remove("source", "new");
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(replacement), _journal);
            Assert.IsFalse(_journal.TryGet("source", "old", out _));
            Assert.IsTrue(_journal.TryGet("source", "new", out _));
        }

        [Test]
        public void DestroyedSourceReleasesColliderProxies()
        {
            var interest = New("Gameplay observer").AddComponent<VegetationInterest>();
            interest.Radius = 5;
            var residency = New("Physics").AddComponent<InstanceColliderResidency>();
            residency.Configure(_container, _prototype, new[] { interest });
            residency.Synchronize();
            Assert.AreEqual(1, residency.ResidentCount);
            Object.DestroyImmediate(_container);
            residency.Synchronize();
            Assert.AreEqual(0, residency.ResidentCount);
            Assert.AreEqual(0, residency.PendingCount);
        }

        [Test]
        public void ColliderResidencyWorksWithoutCameraOrEnabledRenderer()
        {
            var interest = New("Gameplay observer").AddComponent<VegetationInterest>();
            interest.Radius = 5;
            var residency = New("Physics").AddComponent<InstanceColliderResidency>();
            residency.Configure(_container, _prototype, new[] { interest }, 4, 1, 2);
            residency.Synchronize();
            Assert.AreEqual(1, residency.ResidentCount);
            Assert.IsNull(_container.Renderer);
            Assert.AreEqual(1, _container.GetComponentsInChildren<InstanceColliderIdentity>().Length);
            interest.transform.position = Vector3.right * 20;
            residency.Synchronize();
            Assert.AreEqual("b", _container.GetComponentInChildren<InstanceColliderIdentity>().PlacementId);
            _journal.Remove(_container.SourceId, "b");
            residency.Synchronize();
            Assert.AreEqual(0, residency.ResidentCount);
            residency.enabled = false;
            Assert.AreEqual(0, _container.transform.childCount);
        }

        [Test]
        public void DistantColliderSourceSkipsScanAndResumesAfterMovesAndEdits()
        {
            var interest = New("Gameplay observer").AddComponent<VegetationInterest>();
            interest.Radius = 5;
            interest.transform.position = Vector3.right * 200;
            var residency = New("Physics").AddComponent<InstanceColliderResidency>();
            residency.Configure(_container, _prototype, new[] { interest });
            residency.Synchronize();
            Assert.AreEqual(0, residency.ResidentCount);
            _container.transform.position = Vector3.right * 180;
            residency.Synchronize();
            Assert.AreEqual("b", _container.GetComponentInChildren<InstanceColliderIdentity>().PlacementId);
            _journal.Remove(_container.SourceId, "b");
            residency.Synchronize();
            Assert.AreEqual(0, residency.ResidentCount);
            _journal.Override(_container.SourceId, At("b", 20));
            residency.Synchronize();
            Assert.AreEqual(1, residency.ResidentCount);
        }

        [Test]
        public void ColliderBudgetsReportDemandAndDoNotInstantiatePrototypeScripts()
        {
            _prototype.AddComponent<VegetationInterest>();
            var interest = New("Gameplay observer").AddComponent<VegetationInterest>();
            interest.Radius = 100;
            var residency = New("Physics").AddComponent<InstanceColliderResidency>();
            residency.Configure(_container, _prototype, new[] { interest }, 1, 1);
            residency.Synchronize();
            Assert.AreEqual(1, residency.ResidentCount);
            Assert.AreEqual(1, residency.PendingCount);
            Assert.IsNotEmpty(residency.Diagnostic);
            Assert.IsEmpty(_container.GetComponentsInChildren<VegetationInterest>());
            Assert.IsEmpty(_container.GetComponentsInChildren<Renderer>());
        }

        [Test]
        public void CorridorMasksHaveFalloffAndPreserveGroundBelowBridges()
        {
            var mask = New("Road").AddComponent<PlacementExclusion>();
            mask.Configure(new[] { new Vector3(-20, 5, 0), new Vector3(20, 5, 0) }, 2, 2, 1, 2);
            Assert.AreEqual(0, mask.Sample(new Vector3(0, 5, 0)));
            Assert.AreEqual(0.5f, mask.Sample(new Vector3(0, 5, 3)), 0.0001f);
            Assert.AreEqual(1, mask.Sample(Vector3.zero));
            Assert.AreEqual(1, mask.Sample(new Vector3(0, 5, 5)));
            Bounds change = default;
            mask.Changed += bounds => change = bounds;
            mask.transform.position = Vector3.right * 50;
            mask.Refresh();
            Assert.IsTrue(change.Contains(new Vector3(-20, 5, 0)));
            Assert.IsTrue(change.Contains(new Vector3(70, 5, 0)));
            mask.enabled = false;
            Assert.AreEqual(1, mask.Sample(new Vector3(50, 5, 0)));
        }

        [Test]
        public void PackedSnapshotsRoundTripAndRejectTruncationWithoutChangingData()
        {
            var asset = ScriptableObject.CreateInstance<InstancePlacementAsset>();
            var copy = ScriptableObject.CreateInstance<InstancePlacementAsset>();
            try
            {
                asset.SetPlacements(new[] { At("persistent-a", 12.125f), At("persistent-b", -93.5f) });
                var bytes = asset.ExportBinary();
                Assert.Less(bytes.Length, 160);
                copy.ImportBinary(bytes);
                Assert.AreEqual(asset.CopyPlacements(), copy.CopyPlacements());
                Array.Resize(ref bytes, bytes.Length - 1);
                Assert.Throws<System.IO.InvalidDataException>(() => copy.ImportBinary(bytes));
                Assert.AreEqual(2, copy.Count);
            }
            finally
            {
                Object.DestroyImmediate(asset);
                Object.DestroyImmediate(copy);
            }
        }

        [Test]
        public void DirectGroundHeightAndNormalsRemainContinuousAcrossTiles()
        {
            var first = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 32, 32) };
            var second = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 32, 32) };
            try
            {
                var a = new float[33, 33];
                var b = new float[33, 33];
                for (int z = 0; z < 33; z++)
                {
                    for (int x = 0; x < 33; x++)
                    {
                        a[z, x] = x / 128f;
                        b[z, x] = (x + 32) / 128f;
                    }
                }
                first.SetHeights(0, 0, a);
                second.SetHeights(0, 0, b);
                var left = UnityEngine.Terrain.CreateTerrainGameObject(first);
                var right = UnityEngine.Terrain.CreateTerrainGameObject(second);
                SceneManager.MoveGameObjectToScene(left, _scene);
                SceneManager.MoveGameObjectToScene(right, _scene);
                left.GetComponent<UnityEngine.Terrain>().allowAutoConnect = false;
                right.GetComponent<UnityEngine.Terrain>().allowAutoConnect = false;
                right.transform.position = Vector3.right * 32;
                var binding = New("Ground").AddComponent<TerrainGroundBindings>();
                binding.Configure(new[] {
                    new TerrainGroundBindings.Surface { Terrain = left.GetComponent<UnityEngine.Terrain>(), Tint = Color.white },
                    new TerrainGroundBindings.Surface { Terrain = right.GetComponent<UnityEngine.Terrain>(), Tint = Color.white } });
                Assert.IsTrue(binding.TrySample(new Vector3(32, 0, 16), out float height, out var normal));
                Assert.AreEqual(8, height, 0.002f);
                Assert.Less(Vector3.Angle(new Vector3(-0.25f, 1, 0), normal), 0.05f);
                Assert.IsFalse(binding.TrySample(new Vector3(90, 0, 16), out _, out _));
                binding.Publish();
                Assert.AreEqual(2, Shader.GetGlobalInt("_LoogaGroundCount"));
                binding.enabled = false;
                Assert.AreEqual(0, Shader.GetGlobalInt("_LoogaGroundCount"));
            }
            finally
            {
                foreach (var terrain in Object.FindObjectsByType<UnityEngine.Terrain>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (terrain.terrainData == first || terrain.terrainData == second)
                    {
                        Object.DestroyImmediate(terrain.gameObject);
                    }
                }
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        private sealed class PageSource : IVegetationPageSource
        {
            public Task<VegetationPageRecord[]> LoadAsync(Vector2Int cell, bool trees, CancellationToken cancellation) =>
                Task.FromResult(new[] {
                    new VegetationPageRecord(0, Matrix4x4.Translate(Vector3.right * 7), InstanceAppearance.Default),
                    new VegetationPageRecord(1, Matrix4x4.Translate(Vector3.right * 8), InstanceAppearance.Default) });
        }

        [UnityTest]
        public IEnumerator StreamedEditsUseSourceAndPageIdentityAcrossRepeatedLoads()
        {
            var cell = new Vector2Int(1, 2);
            _journal.Remove("page-source-r1", EditedVegetationPageSource.RecordId(cell, true, 0));
            _journal.Override("page-source-r1", At(EditedVegetationPageSource.RecordId(cell, true, 1), 0));
            var source = new EditedVegetationPageSource(new PageSource(), "page-source-r1", _journal);
            for (int i = 0; i < 3; i++)
            {
                var task = source.LoadAsync(cell, true, CancellationToken.None);
                while (!task.IsCompleted) yield return null;
                Assert.IsFalse(task.IsFaulted, task.Exception?.ToString());
                Assert.AreEqual(1, task.Result.Length);
                Assert.AreEqual(1, task.Result[0].Prototype);
                Assert.AreEqual(Matrix4x4.identity, task.Result[0].LocalTransform);
            }
            var unchanged = source.LoadAsync(Vector2Int.zero, true, CancellationToken.None);
            while (!unchanged.IsCompleted) yield return null;
            Assert.AreEqual(2, unchanged.Result.Length);
            Assert.AreEqual(7, unchanged.Result[0].LocalTransform.m03);
        }
    }
}
