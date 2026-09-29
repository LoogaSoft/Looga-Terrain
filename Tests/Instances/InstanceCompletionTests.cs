using LoogaSoft.Instancing;
using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Instances.Tests
{
    public sealed class InstanceCompletionTests
    {
        [Test]
        public void AuthoredStreamIdsSurviveReorderAndRevisionChange()
        {
            string folder = Path.Combine(Path.GetTempPath(), "LoogaPageIds-" + Guid.NewGuid().ToString("N"));
            var first = new VegetationPageRecord(0, Matrix4x4.identity, InstanceAppearance.Default, "oak-a");
            var second = new VegetationPageRecord(0, Matrix4x4.Translate(Vector3.right), InstanceAppearance.Default, "oak-b");
            try
            {
                FileVegetationPageSource.Write(folder, Vector2Int.zero, true, 1, new[] { first, second });
                var loaded = new FileVegetationPageSource(folder, 1).LoadAsync(Vector2Int.zero, true, CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual("oak-a", loaded[0].Id);
                var journal = new InstanceEditJournal();
                journal.Remove("world-tile", "oak-a");
                FileVegetationPageSource.Write(folder, Vector2Int.zero, true, 2, new[] { second, first });
                // Complete the file read before wrapping it to avoid blocking the editor synchronization context.
                var edited = new EditedVegetationPageSource(new CompletedSource(new FileVegetationPageSource(folder, 2)
                    .LoadAsync(Vector2Int.zero, true, CancellationToken.None).GetAwaiter().GetResult()), "world-tile", journal);
                var result = edited.LoadAsync(Vector2Int.zero, true, CancellationToken.None).GetAwaiter().GetResult();
                Assert.AreEqual(1, result.Length);
                Assert.AreEqual("oak-b", result[0].Id);
                Assert.AreEqual(Vector3.right, (Vector3)result[0].LocalTransform.GetColumn(3));
                Assert.Throws<ArgumentException>(() => FileVegetationPageSource.Write(folder, Vector2Int.zero, true, 2, new[] { first, first }));
            }
            finally
            {
                foreach (string file in Directory.GetFiles(folder)) File.Delete(file);
                Directory.Delete(folder);
            }
        }
        private sealed class CompletedSource : IVegetationPageSource
        {
            private readonly VegetationPageRecord[] _records;
            public CompletedSource(VegetationPageRecord[] records) => _records = records;
            public System.Threading.Tasks.Task<VegetationPageRecord[]> LoadAsync(Vector2Int cell, bool trees, CancellationToken cancellation) =>
                System.Threading.Tasks.Task.FromResult(_records);
        }

        [Test]
        public void PackedWhitespaceIdIsRejectedWithoutChangingSnapshot()
        {
            var asset = ScriptableObject.CreateInstance<InstancePlacementAsset>();
            try
            {
                asset.SetPlacements(new[] { new InstanceContainer.Placement { Id = "a", Scale = Vector3.one } });
                byte[] bytes = asset.ExportBinary();
                bytes[10] = 32;
                Assert.Throws<InvalidDataException>(() => asset.ImportBinary(bytes));
                Assert.AreEqual("a", asset.CopyPlacements()[0].Id);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void InvalidGroundTransformMatchesGpuRejection()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(10,10,10) };
            try
            {
                var owner = new GameObject("Ground");
                SceneManager.MoveGameObjectToScene(owner, scene);
                var terrain = owner.AddComponent<UnityEngine.Terrain>();
                terrain.terrainData = data;
                var binding = owner.AddComponent<TerrainGroundBindings>();
                binding.Configure(new[] { new TerrainGroundBindings.Surface { Terrain = terrain, Tint = Color.white } });
                Assert.IsTrue(binding.TrySample(Vector3.one, out _, out _));
                owner.transform.localScale = Vector3.one * 2;
                binding.Publish();
                Assert.IsFalse(binding.TrySample(Vector3.one, out _, out _));
                Assert.IsNotNull(binding.Diagnostic);
                Assert.AreEqual(0, Shader.GetGlobalInt("_LoogaGroundCount"));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(data);
            }
        }
    }
}
