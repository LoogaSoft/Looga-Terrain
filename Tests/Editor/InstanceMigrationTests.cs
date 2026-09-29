using LoogaSoft.Instancing.Editor;
using LoogaSoft.Instancing;
using System;
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
    public sealed class InstanceMigrationTests
    {
        [Test]
        public void StandaloneExportPreservesNativeReferencesAndStripsControllers()
        {
            string folder = "Assets/LoogaMigrationTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            Scene preview = EditorSceneManager.NewPreviewScene();
            Scene active = SceneManager.GetActiveScene();
            bool dirty = active.isDirty;
            try
            {
                var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(source, preview);
                source.SetActive(false);
                source.AddComponent<VegetationInteractor>();
                var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, folder + "/Material.mat");
                var renderer = source.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.forceRenderingOff = true;
                var anchor = new GameObject("Probe anchor");
                anchor.transform.SetParent(source.transform, false);
                renderer.probeAnchor = anchor.transform;
                var output = InstanceMigrationTools.ExportPrototype(source, folder + "/Standalone.prefab");
                Assert.IsNotNull(output);
                Assert.IsEmpty(output.GetComponentsInChildren<MonoBehaviour>(true));
                Assert.IsNotNull(output.GetComponent<BoxCollider>());
                Assert.AreSame(output.transform.Find("Probe anchor"), output.GetComponent<MeshRenderer>().probeAnchor);
                Assert.IsFalse(output.GetComponent<MeshRenderer>().forceRenderingOff);
                Assert.IsTrue(renderer.forceRenderingOff);
                Assert.IsFalse(source.activeSelf);
                Assert.AreSame(anchor.transform, renderer.probeAnchor);
                Assert.IsEmpty(InstanceMigrationTools.FindReplacementDependencies(output));
                AssetDatabase.ImportAsset(folder + "/Standalone.prefab", ImportAssetOptions.ForceUpdate);
                var reloaded = AssetDatabase.LoadAssetAtPath<GameObject>(folder + "/Standalone.prefab");
                Assert.AreSame(reloaded.transform.Find("Probe anchor"), reloaded.GetComponent<MeshRenderer>().probeAnchor);
                Assert.AreEqual(dirty, active.isDirty);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        [Test]
        public void ExportRejectsExternalSceneReferencesAndLeavesNoPartialAsset()
        {
            string folder = "Assets/LoogaMigrationTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(source, preview);
                var external = new GameObject("External anchor");
                SceneManager.MoveGameObjectToScene(external, preview);
                var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, folder + "/Material.mat");
                source.GetComponent<MeshRenderer>().sharedMaterial = material;
                source.GetComponent<MeshRenderer>().probeAnchor = external.transform;
                Assert.Throws<ArgumentException>(() => InstanceMigrationTools.ExportPrototype(source, folder + "/Rejected.prefab"));
                Assert.IsNull(AssetDatabase.LoadMainAssetAtPath(folder + "/Rejected.prefab"));
                Assert.Throws<ArgumentException>(() => InstanceMigrationTools.ExportPrototype(source, null));
                Assert.IsTrue(source.activeSelf);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
