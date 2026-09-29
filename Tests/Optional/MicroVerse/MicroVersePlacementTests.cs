using LoogaSoft.Instancing;
using System.Linq;
using JBooth.MicroVerseCore;
using LoogaSoft.Terrain.Instances;
using LoogaSoft.Terrain.Integrations;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class MicroVersePlacementTests
    {
        [Test]
        public void MaskRefreshRegeneratesOnlyLocalTriangles()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(0, 0, 0), new Vector3(0, 0, 8), new Vector3(8, 0, 0), new Vector3(8, 0, 8),
                    new Vector3(100, 0, 0), new Vector3(100, 0, 8), new Vector3(108, 0, 0), new Vector3(108, 0, 8)
                },
                triangles = new[] { 0, 1, 2, 2, 1, 3, 4, 5, 6, 6, 5, 7 }
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            try
            {
                GameObject Make(string name)
                {
                    var result = new GameObject(name);
                    SceneManager.MoveGameObjectToScene(result, scene);
                    return result;
                }
                var sourceRoot = Make("Empty MicroVerse fixture");
                sourceRoot.SetActive(false);
                var source = sourceRoot.AddComponent<MicroVerse>();
                source.enabled = false;
                source.SaveBackToTerrain();
                Assert.IsEmpty(source.terrains);
                var prototype = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(prototype, scene);
                prototype.SetActive(false);
                prototype.GetComponent<Renderer>().sharedMaterial = material;
                var target = Make("Disabled placement destination").AddComponent<InstanceContainer>();
                target.enabled = false;
                target.Configure(prototype, null);
                var surface = Make("Separated mesh regions").AddComponent<MeshFilter>();
                surface.sharedMesh = mesh;
                var author = Make("Mesh author").AddComponent<MeshScatterAuthoring>();
                author.Configure(surface, new[] { new MeshScatterAuthoring.Target { Container = target, Weight = 1 } },
                    new MeshScatterSettings { Density = 1 });
                var mask = Make("Local road corridor").AddComponent<PlacementExclusion>();
                mask.Configure(new[] { new Vector3(2, 0, 0), new Vector3(2, 0, 8) }, 1, 0);
                author.SetExclusions(new[] { mask });
                author.Regenerate();
                Assert.AreEqual(4, author.LastRegeneratedTriangles);
                var far = target.CopyPlacements().Where(placement => placement.Position.x > 90).ToArray();
                Assert.Greater(far.Length, 0);
                var bridge = Make("Mask bridge").AddComponent<MicroVersePlacementBridge>();
                bridge.Configure(source, null, null, new[] { mask }, new[] { author });
                Assert.IsTrue(bridge.ProcessChanges(), bridge.Diagnostic);
                Assert.AreEqual(2, author.LastRegeneratedTriangles);
                mask.transform.position = Vector3.right;
                MicroVerse.OnFinishedUpdating.Invoke();
                Assert.IsTrue(bridge.ProcessChanges(), bridge.Diagnostic);
                Assert.AreEqual(2, author.LastRegeneratedTriangles);
                CollectionAssert.AreEqual(far, target.CopyPlacements().Where(placement => placement.Position.x > 90).ToArray());
                Assert.IsFalse(bridge.ProcessChanges());
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void PublicCompletionCancelAndUnloadPreserveNativeOwnership()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            try
            {
                var sourceRoot = new GameObject("Empty MicroVerse fixture");
                SceneManager.MoveGameObjectToScene(sourceRoot, scene);
                sourceRoot.SetActive(false);
                var source = sourceRoot.AddComponent<MicroVerse>();
                source.enabled = false;
                var native = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(native, scene);
                var mesh = native.GetComponent<MeshRenderer>();
                mesh.sharedMaterial = material;
                var owner = new GameObject("Looga object receiver");
                SceneManager.MoveGameObjectToScene(owner, scene);
                var provider = owner.AddComponent<SceneInstanceProvider>();
                var bridge = owner.AddComponent<MicroVersePlacementBridge>();
                bridge.Configure(source, provider, new[] { native.transform });
                Assert.IsTrue(bridge.ProcessChanges());
                Assert.IsTrue(mesh.forceRenderingOff);
                // The fixture has no terrains. Use the public synchronization path without a bake.
                source.SaveBackToTerrain();
                Assert.IsEmpty(source.terrains);
                MicroVerse.OnBeginUpdating.Invoke();
                Assert.IsFalse(mesh.forceRenderingOff);
                Assert.IsTrue(bridge.IsWaiting);
                MicroVerse.OnFinishedUpdating.Invoke();
                Assert.IsTrue(bridge.ProcessChanges());
                Assert.IsTrue(mesh.forceRenderingOff);
                MicroVerse.OnBeginUpdating.Invoke();
                MicroVerse.OnCancelUpdating.Invoke();
                Assert.IsTrue(bridge.ProcessChanges());
                Assert.IsTrue(mesh.forceRenderingOff);
                bridge.enabled = false;
                Assert.IsFalse(mesh.forceRenderingOff);
                Assert.IsFalse(bridge.ProcessChanges());
                bridge.enabled = true;
                Assert.IsTrue(bridge.ProcessChanges());
                Object.DestroyImmediate(sourceRoot);
                Assert.IsFalse(bridge.ProcessChanges());
                Assert.IsFalse(mesh.forceRenderingOff);
                Assert.AreEqual(0, provider.ActiveSourceCount);
                Assert.IsTrue(native.activeSelf);
                Assert.IsNotNull(native.GetComponent<Collider>());
                Assert.AreSame(material, mesh.sharedMaterial);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void CapturesAfterLoadAndWaitsForSyncAfterUpdate()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            try
            {
                GameObject Make(string name)
                {
                    var result = new GameObject(name);
                    SceneManager.MoveGameObjectToScene(result, scene);
                    return result;
                }
                MicroVerse MakeSource(string name)
                {
                    GameObject root = Make(name);
                    root.SetActive(false);
                    var result = root.AddComponent<MicroVerse>();
                    result.enabled = false;
                    return result;
                }
                // A new MicroVerse has the same state as after a scene load: not synchronized and not modifying.
                MicroVerse loaded = MakeSource("Loaded MicroVerse fixture");
                Assert.IsFalse(loaded.IsHeightSyncd);
                var native = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(native, scene);
                var mesh = native.GetComponent<MeshRenderer>();
                mesh.sharedMaterial = material;
                GameObject owner = Make("Looga object receiver");
                var provider = owner.AddComponent<SceneInstanceProvider>();
                var bridge = owner.AddComponent<MicroVersePlacementBridge>();
                bridge.Configure(loaded, provider, new[] { native.transform });
                Assert.IsTrue(bridge.ProcessChanges(), bridge.Diagnostic);
                Assert.IsTrue(mesh.forceRenderingOff);

                // After an observed update, the bridge waits for height synchronization.
                MicroVerse.OnBeginUpdating.Invoke();
                MicroVerse.OnFinishedUpdating.Invoke();
                Assert.IsFalse(bridge.ProcessChanges());
                Assert.IsFalse(mesh.forceRenderingOff);
                Assert.IsTrue(bridge.IsWaiting);
                loaded.SaveBackToTerrain();
                Assert.IsTrue(bridge.ProcessChanges(), bridge.Diagnostic);
                Assert.IsTrue(mesh.forceRenderingOff);

                // An update that started before the bridge was enabled also requires height synchronization.
                MicroVerse updating = MakeSource("Updating MicroVerse fixture");
                updating.IsModifyingTerrain = true;
                var late = Make("Late bridge").AddComponent<MicroVersePlacementBridge>();
                late.Configure(updating, null, null);
                Assert.IsFalse(late.ProcessChanges());
                updating.IsModifyingTerrain = false;
                Assert.IsFalse(late.ProcessChanges());
                updating.SaveBackToTerrain();
                Assert.IsTrue(late.ProcessChanges(), late.Diagnostic);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(material);
            }
        }

#if LOOGA_TERRAIN_MICROVERSE_OBJECTS
        [Test]
        public void DataModeCopiesSpawnedTransformsAndKeepsColliders()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var variantMaterial = new Material(material);
            var meshes = new[] { MakeMesh(PrimitiveType.Cube), MakeMesh(PrimitiveType.Sphere), MakeMesh(PrimitiveType.Capsule) };
            try
            {
                GameObject Make(string name)
                {
                    var result = new GameObject(name);
                    SceneManager.MoveGameObjectToScene(result, scene);
                    return result;
                }
                GameObject MakePart(string name, Mesh mesh, Transform parent)
                {
                    var part = new GameObject(name);
                    part.transform.SetParent(parent, false);
                    part.AddComponent<MeshFilter>().sharedMesh = mesh;
                    part.AddComponent<MeshRenderer>().sharedMaterial = material;
                    return part;
                }
                GameObject sourceRoot = Make("Empty MicroVerse fixture");
                sourceRoot.SetActive(false);
                var source = sourceRoot.AddComponent<MicroVerse>();
                source.enabled = false;

                // The prefab has one root LOD group, two LOD renderers and a root collider.
                GameObject prefab = Make("Tree prefab");
                prefab.SetActive(false);
                prefab.AddComponent<BoxCollider>();
                var lods = new[]
                {
                    MakePart("LOD0", meshes[0], prefab.transform).GetComponent<Renderer>(),
                    MakePart("LOD1", meshes[1], prefab.transform).GetComponent<Renderer>()
                };
                prefab.AddComponent<LODGroup>().SetLODs(new[]
                {
                    new LOD(0.5f, new[] { lods[0] }), new LOD(0.1f, new[] { lods[1] })
                });

                Transform output = Make("Stamp output").transform;
                GameObject stampObject = Make("Object stamp");
                stampObject.SetActive(false);
                var stamp = stampObject.AddComponent<ObjectStamp>();
                stamp.prototypes.Add(prefab);
                stamp.parentObject = output;
                var clones = new GameObject[3];
                for (int i = 0; i < clones.Length; i++)
                {
                    clones[i] = Object.Instantiate(prefab, output);
                    // Use world-sized coordinates. Signature matching must not depend on world precision.
                    clones[i].transform.SetPositionAndRotation(new Vector3(6000 + 10 * i, 400, 1000 - 5 * i),
                        Quaternion.Euler(0, 40 * i, 0));
                    clones[i].transform.localScale = Vector3.one * (1 + i);
                    clones[i].SetActive(true);
                }
                // A variant with the same meshes and another material must use its own set entry.
                GameObject variant = Object.Instantiate(prefab);
                variant.name = "Tree prefab variant";
                SceneManager.MoveGameObjectToScene(variant, scene);
                variant.GetComponentInChildren<MeshRenderer>(true).sharedMaterial = variantMaterial;
                stamp.prototypes.Add(variant);
                GameObject variantClone = Object.Instantiate(variant, output);
                variantClone.transform.position = new Vector3(6040, 400, 1000);
                variantClone.SetActive(true);
                GameObject unmatched = MakePart("Unmatched output", meshes[2], output);

                var bridge = Make("Data bridge").AddComponent<MicroVersePlacementBridge>();
                bridge.ConfigureData(source, new[] { stamp }, true);
                Assert.AreEqual(MicroVerseObjectOutputMode.Data, bridge.Mode);
                for (int step = 0; step < 4 && bridge.ProcessChanges(); step++)
                {
                }
                MicroVerseObjectDataReport report = bridge.LastDataCapture;
                Assert.IsNotNull(report, bridge.Diagnostic);
                Assert.AreEqual(5, report.NativeRoots);
                Assert.AreEqual(1, report.UnmatchedRoots);
                Assert.AreEqual(4, report.Placements);
                Assert.AreEqual(2, report.Entries);
                InstanceSet set = bridge.DataSet;
                Assert.IsNotNull(set);
                Assert.AreEqual(2, set.EntryCount);
                Assert.AreEqual(1, set.GetPlacementCount(stamp, variant));
                InstanceSet.Placement[] placements = set.CopyPlacements(stamp, prefab);
                Assert.AreEqual(3, placements.Length);
                for (int i = 0; i < clones.Length; i++)
                {
                    Assert.Less(Vector3.Distance(clones[i].transform.position, placements[i].Position), 0.001f);
                    Assert.Less(Vector3.Distance(clones[i].transform.lossyScale, placements[i].Scale), 0.0001f);
                    Assert.Less(Quaternion.Angle(clones[i].transform.rotation, Quaternion.Euler(placements[i].EulerAngles)), 0.01f);
                    // The strip keeps the root and its collider.
                    Assert.AreEqual(0, clones[i].transform.childCount);
                    Assert.IsNull(clones[i].GetComponent<LODGroup>());
                    Assert.IsNotNull(clones[i].GetComponent<BoxCollider>());
                }
                Assert.IsNotNull(unmatched.GetComponent<MeshRenderer>());
                InstanceSourceAdapterStatus status = bridge.SourceStatus;
                Assert.AreEqual(4, status.LoogaOwnedSources);
                Assert.AreEqual(1, status.NativeFallbackSources);

                // Stripped roots mean that MicroVerse did not respawn the stamp. The placements stay.
                bridge.RequestRefresh();
                bridge.ProcessChanges();
                Assert.AreSame(report, bridge.LastDataCapture);
                Assert.AreEqual(3, set.GetPlacementCount(stamp, prefab));

                // An empty output means that the stamp spawned nothing. The set entries are removed.
                foreach (GameObject clone in clones)
                {
                    Object.DestroyImmediate(clone);
                }
                Object.DestroyImmediate(variantClone);
                Object.DestroyImmediate(unmatched);
                bridge.RequestRefresh();
                for (int step = 0; step < 4 && bridge.ProcessChanges(); step++)
                {
                }
                Assert.AreEqual(0, set.EntryCount);
                Assert.AreEqual(0, set.PlacementCount);

                // Scene Objects mode disables the data set.
                bridge.Configure(source, null, null);
                Assert.AreEqual(MicroVerseObjectOutputMode.SceneObjects, bridge.Mode);
                Assert.IsFalse(set.enabled);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(variantMaterial);
                foreach (Mesh mesh in meshes)
                {
                    Object.DestroyImmediate(mesh);
                }
            }
        }

        private static Mesh MakeMesh(PrimitiveType type)
        {
            GameObject primitive = GameObject.CreatePrimitive(type);
            Mesh mesh = Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
            Object.DestroyImmediate(primitive);
            return mesh;
        }
#endif

        [Test]
        public void FirstMicroVerseUpdateReportsHeightSynchronization()
        {
            // This test requires the Kubera World local patch in MicroVerse.Modify.
            // MicroVerse 1.7.0 synchronizes heights on the first update but does not set IsHeightSyncd.
            Scene scene = EditorSceneManager.NewPreviewScene();
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(64, 32, 64) };
            try
            {
                var root = new GameObject("First update MicroVerse fixture");
                SceneManager.MoveGameObjectToScene(root, scene);
                root.SetActive(false);
                GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(data);
                SceneManager.MoveGameObjectToScene(tile, scene);
                tile.transform.SetParent(root.transform);
                var terrain = tile.GetComponent<UnityEngine.Terrain>();
                terrain.allowAutoConnect = false;
                var source = root.AddComponent<MicroVerse>();
                source.explicitTerrains = new[] { terrain };
                var bridge = root.AddComponent<MicroVersePlacementBridge>();
                bridge.Configure(source, null, null);
                root.SetActive(true);
                Assert.IsFalse(source.IsHeightSyncd);

                source.Modify(false, true);
                Assert.IsTrue(bridge.IsWaiting);
                Assert.IsTrue(source.IsHeightSyncd);
                Assert.IsFalse(bridge.ProcessChanges());
                // MicroVerse clears this flag in LateUpdate. The edit-mode test sets it through the public setter.
                source.IsModifyingTerrain = false;
                Assert.IsTrue(bridge.ProcessChanges(), bridge.Diagnostic);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(data);
            }
        }
    }
}
