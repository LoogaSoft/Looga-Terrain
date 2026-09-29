using System;
using System.Linq;
using JBooth.MicroVerseCore;
using LoogaSoft.Instancing;
using LoogaSoft.Instancing.Splines;
using LoogaSoft.Terrain.Integrations;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class MicroVerseRoadWorkflowTests
    {
        [Test]
        public void GeneratedRoadAndObjectsRebuildWithoutTouchingTerrain()
        {
            string folder = "Assets/LoogaRoadWorkflow_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            Scene scene = EditorSceneManager.NewPreviewScene();
            RoadConfig config = null;
            try
            {
                var root = new GameObject("Isolated MicroVerse output");
                SceneManager.MoveGameObjectToScene(root, scene);
                root.SetActive(false);
                var source = root.AddComponent<MicroVerse>();
                source.enabled = false;
                source.SaveBackToTerrain();
                Assert.IsEmpty(source.terrains);
                var piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(piece, scene);
                var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, folder + "/Road.mat");
                piece.GetComponent<Renderer>().sharedMaterial = material;
                piece.transform.localScale = new Vector3(4, 0.2f, 4);
                var prefab = PrefabUtility.SaveAsPrefabAsset(piece, folder + "/Piece.prefab");
                Object.DestroyImmediate(piece);
                var roadRoot = new GameObject("Generated road");
                SceneManager.MoveGameObjectToScene(roadRoot, scene);
                var spline = roadRoot.AddComponent<SplineContainer>();
                spline.Spline = new Spline(new[] { new BezierKnot(new Vector3(0, 0, 0)), new BezierKnot(new Vector3(0, 0, 24)) });
                var road = roadRoot.AddComponent<Road>();
                road.splineContainer = spline;
                road.modifiesTerrain = false;
                config = ScriptableObject.CreateInstance<RoadConfig>();
                config.entries = new[] {new RoadConfig.Entry {size = 4, prefab = prefab, overlays = Array.Empty<RoadConfig.Overlay>()}};
                config.sharedOverlays = Array.Empty<RoadConfig.Overlay>();
                road.config = config;
                road.Generate(null, false);
                Assert.Greater(road.children.Count, 1);
                var receiver = new GameObject("Looga road receiver");
                SceneManager.MoveGameObjectToScene(receiver, scene);
                var mask = receiver.AddComponent<PlacementExclusion>();
                var corridor = receiver.AddComponent<SplinePlacementBridge>();
                corridor.Configure(spline, mask, 2, 0);
                var objects = receiver.AddComponent<SceneInstanceProvider>();
                var bridge = receiver.AddComponent<MicroVersePlacementBridge>();
                bridge.Configure(source, objects, new[] {roadRoot.transform});
                Assert.IsTrue(bridge.ProcessChanges(), bridge.Diagnostic);
                Assert.Greater(objects.ActiveSourceCount, 1, objects.Diagnostic);
                var old = roadRoot.GetComponentsInChildren<MeshRenderer>();
                Assert.IsTrue(old.All(renderer => renderer.forceRenderingOff));
                MicroVerse.OnBeginUpdating.Invoke();
                Assert.IsTrue(old.All(renderer => !renderer.forceRenderingOff));
                for (int i = 0; i < spline.Spline.Count; i++)
                {
                    var knot = spline.Spline[i]; knot.Position.x += 12; spline.Spline[i] = knot;
                }
                road.Generate(null, false);
                corridor.ProcessChanges();
                MicroVerse.OnFinishedUpdating.Invoke();
                Assert.IsTrue(bridge.ProcessChanges(), bridge.Diagnostic);
                Assert.Greater(objects.ActiveSourceCount, 1, objects.Diagnostic);
                Assert.AreEqual(1, mask.Sample(new Vector3(0, 0, 12)));
                Assert.Zero(mask.Sample(new Vector3(12, 0, 12)));
                Assert.IsTrue(old.All(renderer => !renderer));
                bridge.enabled = false;
                Assert.IsTrue(roadRoot.GetComponentsInChildren<MeshRenderer>().All(renderer => !renderer.forceRenderingOff));
                Assert.IsEmpty(source.terrains);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                if (config) Object.DestroyImmediate(config);
                AssetDatabase.DeleteAsset(folder);
            }
        }
    }
}
