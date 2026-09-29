using LoogaSoft.Instancing.Editor;
using LoogaSoft.Instancing;
using System;
using System.Collections;
using UnityEngine.TestTools;
using LoogaSoft.Terrain.Editor;
using LoogaSoft.Terrain.Instances;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class InstanceConversionTests
    {
        [UnityTest]
        public IEnumerator SavedSnapshotReloadRetainsReviewSwitchAndPersistentIdentity()
        {
            string folder = "Assets/LoogaSnapshotTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            Scene previous = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(previous.path))
            {
                // Save the test runner scene so Unity permits an additive saved-scene fixture.
                EditorSceneManager.SaveScene(previous, folder + "/Runner.unity");
            }
            Scene scene = default;

            try
            {
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, folder + "/Material.mat");
                var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
                root.name = "Original snapshot source";
                SceneManager.MoveGameObjectToScene(root, scene);
                root.GetComponent<MeshRenderer>().sharedMaterial = material;
                var prototype = InstanceMigrationTools.ExportPrototype(root, folder + "/Prototype.prefab");
                EditorSceneManager.SaveScene(scene, folder + "/Source.unity");
                var container = InstanceConversionTools.BakeSnapshot(prototype, new[] { root });
                var preview = container.GetComponent<InstanceSnapshotPreview>();
                string id = container.CopyPlacements()[0].Id;
                Assert.IsTrue(preview.SetSnapshotActive(true), preview.Diagnostic);
                EditorUtility.SetDirty(preview);
                EditorUtility.SetDirty(container);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                EditorSceneManager.CloseScene(scene, true);
                scene = EditorSceneManager.OpenScene(folder + "/Source.unity", OpenSceneMode.Additive);
                foreach (var owner in scene.GetRootGameObjects())
                {
                    if (owner.TryGetComponent<InstanceSnapshotPreview>(out var restored))
                    {
                        preview = restored;
                    }
                    if (owner.name == "Original snapshot source")
                    {
                        root = owner;
                    }
                }
                for (int frame = 0; frame < 5 && !preview.IsSnapshotActive; frame++)
                {
                    EditorApplication.QueuePlayerLoopUpdate();
                    yield return null;
                }
                Assert.IsTrue(preview.IsSnapshotActive, preview.Diagnostic);
                Assert.AreEqual(id, preview.GetComponent<InstanceContainer>().CopyPlacements()[0].Id);
                Assert.IsTrue(root.GetComponent<MeshRenderer>().forceRenderingOff);
                Assert.IsTrue(preview.SetSnapshotActive(false));
                Assert.IsFalse(root.GetComponent<MeshRenderer>().forceRenderingOff);
                Assert.IsTrue(root.GetComponent<Collider>().enabled);
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
                if (previous.IsValid() && previous.isLoaded)
                {
                    SceneManager.SetActiveScene(previous);
                }
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void SnapshotPreviewRestoresOriginalsAndRejectsDuplicateOwnership()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            try
            {
                var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(root, scene);
                var native = root.GetComponent<MeshRenderer>();
                native.sharedMaterial = material;
                var owner = new GameObject("Static snapshot");
                SceneManager.MoveGameObjectToScene(owner, scene);
                var container = owner.AddComponent<InstanceContainer>();
                container.enabled = false;
                container.Configure(root, new[] { new InstanceContainer.Placement
                {
                    Id = "reviewed-placement", Position = Vector3.zero, Scale = Vector3.one
                } });
                var preview = owner.AddComponent<InstanceSnapshotPreview>();
                preview.Configure(new[] { root });
                Assert.Throws<ArgumentException>(() => preview.Configure(new[] { root, root }));
                Assert.IsFalse(native.forceRenderingOff);
                Assert.IsTrue(preview.SetSnapshotActive(true), preview.Diagnostic);
                Assert.IsTrue(preview.IsSnapshotActive);
                Assert.IsTrue(native.forceRenderingOff);
                Assert.IsTrue(root.activeSelf);
                Assert.IsTrue(root.GetComponent<Collider>().enabled);
                var otherOwner = new GameObject("Conflicting owner");
                SceneManager.MoveGameObjectToScene(otherOwner, scene);
                var other = otherOwner.AddComponent<SceneInstanceProvider>();
                other.Configure(new[] { root });
                Assert.Zero(other.ActiveSourceCount);
                Assert.IsNotNull(other.Diagnostic);
                Assert.IsTrue(preview.SetSnapshotActive(false));
                Assert.IsFalse(native.forceRenderingOff);
                Assert.IsFalse(container.enabled);
                Assert.IsNull(container.Renderer);
                other.Rebuild();
                Assert.AreEqual(1, other.ActiveSourceCount);
                Assert.IsFalse(preview.SetSnapshotActive(true));
                Assert.IsTrue(native.forceRenderingOff, "Failed preview must not release another owner.");
                other.enabled = false;
                Assert.IsTrue(preview.SetSnapshotActive(true), preview.Diagnostic);
                preview.enabled = false;
                Assert.IsFalse(native.forceRenderingOff);
                Assert.IsFalse(container.enabled);
                preview.enabled = true;
                Assert.IsTrue(preview.IsSnapshotActive);
                Object.DestroyImmediate(owner);
                Assert.IsFalse(native.forceRenderingOff);
                Assert.AreSame(material, native.sharedMaterial);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void ConversionUndoRestoresNativeObjectsAndRejectsInvalidSelection()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            SceneInstanceProvider provider = null;
            try
            {
                var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(root, scene);
                var renderer = root.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                Assert.Throws<ArgumentException>(() => InstanceConversionTools.Convert(new GameObject[] { null }));
                Assert.Throws<ArgumentException>(() => InstanceConversionTools.Convert(new[] { root, root }));
                Assert.IsFalse(renderer.forceRenderingOff);
                provider = InstanceConversionTools.Convert(new[] { root });
                Assert.AreEqual(1, provider.ActiveSourceCount);
                Assert.IsTrue(renderer.forceRenderingOff);
                Assert.IsTrue(root.activeSelf);
                Assert.IsNotNull(root.GetComponent<Collider>());
                Undo.PerformUndo();
                Assert.IsTrue(provider == null);
                Assert.IsFalse(renderer.forceRenderingOff);
                Assert.AreSame(material, renderer.sharedMaterial);
                Assert.IsTrue(root.activeSelf);
            }
            finally
            {
                if (provider)
                {
                    Undo.ClearUndo(provider.gameObject);
                }
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(material);
            }
        }
    }
}
