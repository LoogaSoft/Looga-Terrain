using System.Collections.Generic;
using LoogaSoft.Instancing;
using NatureManufacture.RAM;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Integrations.Tests
{
    public sealed class RamGeneratedWorkflowTests
    {
        [Test]
        public void GeneratedRiverControlPointEditUndoAndReplacementUpdateOnlyTheMask()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var meshes = new HashSet<Mesh>();
            SplineProfile profile = null;
            try
            {
                var river = RamSpline.CreateSpline(material, new List<Vector4>
                {
                    new Vector4(0, 0, 0, 8), new Vector4(0, 0, 12, 8), new Vector4(0, 0, 24, 8)
                }, "Isolated generated RAM river", false);
                SceneManager.MoveGameObjectToScene(river.gameObject, scene);
                profile = river.BaseProfile;
                river.GenerateSpline();
                Assert.Greater(river.MainMeshFilter.sharedMesh.vertexCount, 0);
                meshes.Add(river.MainMeshFilter.sharedMesh);
                var owner = new GameObject("RAM placement receiver");
                SceneManager.MoveGameObjectToScene(owner, scene);
                var mask = owner.AddComponent<PlacementExclusion>();
                var bridge = owner.AddComponent<RamPlacementBridge>();
                bridge.Configure(river, null, mask, 2, 2);
                Assert.IsNull(bridge.Diagnostic);
                Assert.Zero(mask.Sample(new Vector3(0, 0, 12)));
                Undo.IncrementCurrentGroup();
                Undo.RegisterCompleteObjectUndo(river.NmSpline, "Move RAM control points");
                foreach (var point in river.NmSpline.MainControlPoints)
                {
                    point.position.x += 20;
                }
                Undo.FlushUndoRecordObjects();
                river.GenerateSpline();
                meshes.Add(river.MainMeshFilter.sharedMesh);
                bridge.ProcessChanges();
                Assert.IsNull(bridge.Diagnostic);
                Assert.AreEqual(1, mask.Sample(new Vector3(0, 0, 12)));
                Assert.Zero(mask.Sample(new Vector3(20, 0, 12)));
                Undo.PerformUndo();
                river.GenerateSpline();
                meshes.Add(river.MainMeshFilter.sharedMesh);
                bridge.ProcessChanges();
                Assert.Zero(mask.Sample(new Vector3(0, 0, 12)));
                bridge.enabled = false;
                Assert.AreEqual(1, mask.Sample(new Vector3(0, 0, 12)));
                bridge.enabled = true;
                bridge.ProcessChanges();
                Assert.Zero(mask.Sample(new Vector3(0, 0, 12)));
                Object.DestroyImmediate(river.gameObject);
                bridge.ProcessChanges();
                Assert.AreEqual(1, mask.Sample(new Vector3(0, 0, 12)));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                foreach (var mesh in meshes)
                {
                    if (mesh && !AssetDatabase.Contains(mesh))
                    {
                        Object.DestroyImmediate(mesh);
                    }
                }
                if (profile) Object.DestroyImmediate(profile);
                Object.DestroyImmediate(material);
            }
        }
    }
}
