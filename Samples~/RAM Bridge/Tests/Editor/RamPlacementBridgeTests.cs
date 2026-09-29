using LoogaSoft.Instancing;
using LoogaSoft.Terrain.Instances;
using NatureManufacture.RAM;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Integrations.Tests
{
    public sealed class RamPlacementBridgeTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void GenerationEventsRefreshMasksAndDisableReleasesOwnership(bool useLake)
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            var mesh = new Mesh();
            try
            {
                var root = new GameObject("RAM fixture");
                SceneManager.MoveGameObjectToScene(root, scene);
                root.SetActive(false);
                var filter = root.AddComponent<MeshFilter>();
                mesh.vertices = new[] { new Vector3(-10, 0, -10), new Vector3(0, 0, 10), new Vector3(10, 0, -10) };
                mesh.triangles = new[] { 0, 1, 2 };
                mesh.RecalculateBounds();
                filter.sharedMesh = mesh;
                RamSpline river = null;
                LakePolygon lake = null;
                var finished = new UnityEvent();
                if (useLake)
                {
                    lake = root.AddComponent<LakePolygon>();
                    lake.MainMeshFilter = filter;
                    lake.OnGenerationEnded = finished;
                }
                else
                {
                    river = root.AddComponent<RamSpline>();
                    river.MainMeshFilter = filter;
                    river.OnGenerationEnded = finished;
                }
                var owner = new GameObject("Looga receiver");
                SceneManager.MoveGameObjectToScene(owner, scene);
                var mask = owner.AddComponent<PlacementExclusion>();
                var bridge = owner.AddComponent<RamPlacementBridge>();
                bridge.Configure(river, lake, mask, 1, 1);
                Assert.IsNull(bridge.Diagnostic);
                Assert.AreEqual(0, mask.Sample(Vector3.zero));
                var vertices = mesh.vertices;
                for (int i = 0; i < vertices.Length; i++)
                {
                    vertices[i].y = 10;
                }
                mesh.vertices = vertices;
                mesh.RecalculateBounds();
                finished.Invoke();
                Assert.AreEqual(0, mask.Sample(Vector3.zero), "Do not read RAM mesh data inside its callback.");
                bridge.ProcessChanges();
                Assert.AreEqual(1, mask.Sample(Vector3.zero));
                Assert.AreEqual(0, mask.Sample(Vector3.up * 10));
                CollectionAssert.AreEqual(vertices, mesh.vertices);
                bridge.enabled = false;
                finished.Invoke();
                bridge.ProcessChanges();
                Assert.AreEqual(1, mask.Sample(Vector3.up * 10));
                bridge.enabled = true;
                bridge.ProcessChanges();
                Assert.AreEqual(0, mask.Sample(Vector3.up * 10));
                Object.DestroyImmediate(root);
                bridge.ProcessChanges();
                Assert.IsNotEmpty(bridge.Diagnostic);
                Assert.AreEqual(1, mask.Sample(Vector3.up * 10));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
