using LoogaSoft.Instancing;
using System;
using System.Collections;
using UnityEngine.TestTools;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Instances.Tests
{
    public sealed class InstanceRendererTests
    {
        private Action _pendingGraphicsCleanup;

        [UnityTearDown]
        public IEnumerator ReleaseGraphicsFixtureAfterPipelineSwitch()
        {
            if (_pendingGraphicsCleanup == null) yield break;
            // Batch-mode editors have no Scene view camera to perform the pending pipeline switch.
            var switchScene = EditorSceneManager.NewPreviewScene();
            var switchCamera = new GameObject("Pipeline restoration camera").AddComponent<Camera>();
            SceneManager.MoveGameObjectToScene(switchCamera.gameObject, switchScene);
            var switchTarget = RenderTexture.GetTemporary(16, 16, 24);
            try
            {
                switchCamera.cullingMask = 0;
                if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)
                {
                    RenderPipeline.SubmitRenderRequest(switchCamera,
                        new UniversalRenderPipeline.SingleCameraRequest { destination = switchTarget });
                }
                else
                {
                    switchCamera.targetTexture = switchTarget;
                    switchCamera.Render();
                }
            }
            finally
            {
                switchCamera.targetTexture = null;
                EditorSceneManager.ClosePreviewScene(switchScene);
                RenderTexture.ReleaseTemporary(switchTarget);
            }
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (!RenderPipelineManager.pipelineSwitchCompleted && EditorApplication.timeSinceStartup < deadline)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
                yield return null;
            }
            Assert.IsTrue(RenderPipelineManager.pipelineSwitchCompleted,
                "The fixture pipeline is still active. Its resources remain retained until the pipeline switches.");
            Action cleanup = _pendingGraphicsCleanup;
            _pendingGraphicsCleanup = null;
            cleanup();
            yield return null;
        }

        [Test]
        public void StableHandlesSurviveGrowthAndRejectStaleOrForeignUpdates()
        {
            GameObject source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            source.GetComponent<Renderer>().sharedMaterial = material;
            try
            {
                var prototype = InstancePrototype.FromPrefab(source);
                using var renderer = new InstanceRenderer();
                using var foreign = new InstanceRenderer();
                int index = renderer.Register(prototype);
                Assert.AreEqual(index, renderer.Register(prototype));
                InstanceHandle first = renderer.Add(index, Matrix4x4.identity);
                renderer.Flush();
                for (int i = 0; i < 160; i++)
                {
                    renderer.Add(index, Matrix4x4.Translate(new Vector3(i * 3, 0, 0)));
                }
                renderer.Flush();
                Assert.IsTrue(renderer.Update(first, Matrix4x4.Translate(Vector3.up)));
                Assert.IsFalse(foreign.Remove(first));
                renderer.Flush();
                long uploaded = renderer.UploadedBytes;
                renderer.Flush();
                Assert.AreEqual(uploaded, renderer.UploadedBytes);
                Assert.IsTrue(renderer.Remove(first));
                InstanceHandle replacement = renderer.Add(index, Matrix4x4.identity);
                Assert.IsFalse(renderer.Update(first, Matrix4x4.identity));
                Assert.IsTrue(renderer.Update(replacement, Matrix4x4.Scale(new Vector3(-1, 2, 1))));
                renderer.Flush();
                Assert.AreEqual(161, renderer.InstanceCount);
                Assert.AreEqual(240, renderer.UploadedBytes - uploaded);
                Assert.Throws<ArgumentException>(() => renderer.Add(index, Matrix4x4.Scale(Vector3.zero)));
                renderer.Dispose();
                renderer.Dispose();
                Assert.AreEqual(0, renderer.GpuBytes);
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(material);
            }
        }

        private static readonly int[] ImageCases = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 };

        [UnityTest]
        public IEnumerator NativeAndIndirectImagesMatchWithLodChildTransformsAndMirroredScale([ValueSource(nameof(ImageCases))] int scenario)
        {
            switch (scenario)
            {
                case 0: return RunImage(false, false, false, false, false, 0, 2);
                case 1: return RunImage(true, false, false, false, false, 0, 2);
                case 2: return RunImage(false, true, false, false, false, 0, 2);
                case 3: return RunImage(true, true, false, false, false, 0, 2);
                case 4: return RunImage(false, false, true, false, false, 0, 2);
                case 5: return RunImage(true, true, true, false, false, 0, 2);
                case 6: return RunImage(false, false, true, true, false, 0, 2);
                case 7: return RunImage(true, true, true, true, false, 0, 2);
                case 8: return RunImage(false, false, true, true, true, 0, 2);
                case 9: return RunImage(true, false, true, true, true, 0, 2);
                case 10: return RunImage(false, false, false, false, false, 1, 2);
                case 11: return RunImage(true, false, false, false, false, 1, 3);
                case 12: return RunImage(false, false, true, true, false, 6, 2);
                case 13: return RunImage(false, false, true, true, false, 6, 3);
                case 14: return RunImage(false, false, true, true, false, 8, 2);
                case 15: return RunImage(false, false, true, true, false, 8, 3);
                default: throw new ArgumentOutOfRangeException(nameof(scenario));
            }
        }

        private static readonly string[] GraphicsShaders = { "LoogaSoft/Lit", "LoogaSoft/Lit Lite", "LoogaSoft/Lit Detail", "LoogaSoft/Skin" };

        [UnityTest]
        public IEnumerator StandardCustomShaderWorksWithoutProfile([Values(2, 3)] int mode)
        {
            return RunImage(false, false, false, false, false, 0, mode, "Hidden/Looga/Tests/Generic BRG");
        }

        [UnityTest]
        public IEnumerator OptionalGraphicsProMaterialsMatchNative([Values(0, 1, 2, 3)] int shader, [Values(2, 3)] int mode)
        {
            if (!Shader.Find(GraphicsShaders[shader]))
            {
                Assert.Ignore("Graphics Pro is optional.");
            }
            return RunImage(false, false, false, false, false, 0, mode, GraphicsShaders[shader], true);
        }

        [UnityTest]
        public IEnumerator GraphicsProFoliageImagesMatchNative([Values("LoogaSoft/Foliage", "LoogaSoft/Grass", "LoogaSoft/Bark")] string shaderName, [Values(2, 3)] int mode)
        {
            if (!Shader.Find(shaderName))
            {
                Assert.Ignore("Graphics Pro is optional.");
            }
            return RunImage(false, false, false, false, false, 0, mode, shaderName, true);
        }

        [UnityTest]
        public IEnumerator GroundAndInteractionImagesMatchNative([Values(64, 128, 384)] int feature, [Values(2, 3)] int mode)
        {
            return RunImage(false, false, false, true, false, feature, mode);
        }

        [UnityTest]
        public IEnumerator MovingContainerImagesMatchNative([Values(2, 3)] int mode)
        {
            return RunImage(false, false, false, false, false, 16, mode);
        }

        [UnityTest]
        public IEnumerator PaintedMeshScatterImagesMatchNative([Values(2, 3)] int mode)
        {
            return RunImage(false, false, false, false, false, 32, mode);
        }

        private IEnumerator RunImage(bool orthographic, bool crossFade, bool tint, bool vegetation, bool billboard, int features, int mode,
            string shaderName = null, bool graphicsPro = false)
        {
            Scene previousScene = SceneManager.GetActiveScene();
            bool ownsLightmapScene = false;
            Scene scene;
            if ((features & 8) != 0)
            {
                scene = previousScene;
                if (!string.IsNullOrEmpty(scene.path))
                {
                    scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                    SceneManager.SetActiveScene(scene);
                    ownsLightmapScene = true;
                }
            }
            else
            {
                scene = EditorSceneManager.NewPreviewScene();
            }

            var existingRoots = new System.Collections.Generic.HashSet<GameObject>(scene.GetRootGameObjects());
            var oldPipeline = GraphicsSettings.defaultRenderPipeline;
            var oldQuality = QualitySettings.renderPipeline;
            bool oldAsync = ShaderUtil.allowAsyncCompilation;
            var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            var rendererSettings = new SerializedObject(rendererData);
            rendererSettings.FindProperty("m_RenderingMode").intValue = mode;
            rendererSettings.ApplyModifiedPropertiesWithoutUndo();
            ScriptableRendererFeature companion = null;
            if (graphicsPro)
            {
                companion = ScriptableObject.CreateInstance("LoogaSoft.Lighting.LoogaLightingFeature") as ScriptableRendererFeature;
                Assert.IsNotNull(companion);
                companion.hideFlags = HideFlags.HideAndDontSave;
                rendererData.rendererFeatures.Add(companion);
                companion.Create();
            }
            var pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            var material = new Material(Shader.Find(shaderName ?? (vegetation ? "Looga/Terrain/Vegetation Lit" : "Universal Render Pipeline/Unlit")));
            material.SetColor("_BaseColor", new Color(0.17f, 0.8f, 0.35f));
            if (vegetation)
            {
                material.SetVector("_BranchWind", new Vector4(0.12f, 2, 3, 0));
                material.SetVector("_LeafWind", new Vector4(0.08f, 5, 2, 0));
                material.SetFloat("_WindTime", 1.7f);
                material.SetFloat("_Billboard", billboard ? 1 : 0);
            }
            var target = new RenderTexture(256, 256, 24);
            var read = new Texture2D(256, 256, TextureFormat.RGBA32, false);
            InstanceRenderer instances = null;
            InstanceContainer scatterTarget = null;
            InstanceContainer.Placement[] scatterPlacements = null;
            var ownedMeshes = new System.Collections.Generic.List<Mesh>();
            var oldLightmaps = LightmapSettings.lightmaps;
            var oldLightmapMode = LightmapSettings.lightmapsMode;
            Texture2D lightmap = null;
            TerrainData groundData = null;
            GraphicsBuffer groundTable = null;
            Texture2DArray[] groundPages = null;
            var oldAmbient = RenderSettings.ambientMode;
            var oldAmbientColor = RenderSettings.ambientLight;
            var oldProbe = RenderSettings.ambientProbe;
            try
            {
                foreach (Object resource in new Object[] { rendererData, pipeline, material, target, read })
                {
                    resource.hideFlags = HideFlags.HideAndDontSave;
                }
                GraphicsSettings.defaultRenderPipeline = QualitySettings.renderPipeline = pipeline;
                ShaderUtil.allowAsyncCompilation = false;
                if (vegetation || graphicsPro)
                {
                    var sun = new GameObject("Validation sun").AddComponent<Light>();
                    SceneManager.MoveGameObjectToScene(sun.gameObject, scene);
                    sun.type = LightType.Directional;
                    sun.intensity = 2;
                    sun.cullingMask = 1 << 30;
                    sun.transform.rotation = Quaternion.Euler(35, -20, 0);
                }
                if ((features & 4) != 0)
                {
                    var fieldObject = new GameObject("Shared wind");
                    SceneManager.MoveGameObjectToScene(fieldObject, scene);
                    fieldObject.AddComponent<VegetationWindField>().Configure(new Vector3(0.2f, 0, 1), 0.7f, 0.3f, 2,
                        new[] { new VegetationWindField.Volume { Transform = fieldObject.transform, Radius = 5, Influence = 0.8f } });
                }
                if ((features & 64) != 0)
                {
                    var interactor = new GameObject("Interaction sphere");
                    SceneManager.MoveGameObjectToScene(interactor, scene);
                    interactor.transform.position = new Vector3(-3, 0, -1);
                    interactor.AddComponent<VegetationInteractor>().Configure(8, 1);
                    material.SetVector("_InteractionParams", new Vector4(1.5f, 0, 0, 0));
                }
                if ((features & 128) != 0)
                {
                    groundData = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 4, 32) };
                    var heights = new float[33, 33];
                    for (int z = 0; z < 33; z++)
                    {
                        for (int x = 0; x < 33; x++)
                        {
                            heights[z, x] = 0.2f + x * 0.008f;
                        }
                    }
                    groundData.SetHeights(0, 0, heights);
                    var terrainObject = new GameObject("Direct height source");
                    SceneManager.MoveGameObjectToScene(terrainObject, scene);
                    terrainObject.transform.position = new Vector3(-16, -1, -2);
                    var terrain = terrainObject.AddComponent<UnityEngine.Terrain>();
                    terrain.allowAutoConnect = false;
                    terrain.terrainData = groundData;
                    terrain.drawHeightmap = false;
                    terrain.drawTreesAndFoliage = false;
                    var bindings = terrainObject.AddComponent<TerrainGroundBindings>();
                    bindings.Configure(new[] { new TerrainGroundBindings.Surface { Terrain = terrain, Tint = new Color(0.4f, 0.8f, 0.1f) } });
                    bindings.Publish();
                    material.SetVector("_GroundParams", new Vector4(0.8f, 0.7f, 1, 3));
                }
                if ((features & 8) != 0)
                {
                    RenderSettings.ambientMode = AmbientMode.Flat;
                    RenderSettings.ambientLight = Color.black;
                    RenderSettings.ambientProbe = default;
                    lightmap = new Texture2D(4, 4, TextureFormat.RGBAHalf, false, true);
                    var colors = new Color[16];
                    for (int i = 0; i < colors.Length; i++) colors[i] = new Color(0.2f + i * 0.03f, 0.8f, 0.3f, 1);
                    lightmap.SetPixels(colors); lightmap.Apply();
                    LightmapSettings.lightmapsMode = LightmapsMode.NonDirectional;
                    LightmapSettings.lightmaps = new[] { new LightmapData { lightmapColor = lightmap } };
                }
                var root = new GameObject("Prototype");
                SceneManager.MoveGameObjectToScene(root, scene);
                var high = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var low = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                SceneManager.MoveGameObjectToScene(high, scene);
                SceneManager.MoveGameObjectToScene(low, scene);
                high.transform.SetParent(root.transform, false);
                low.transform.SetParent(root.transform, false);
                high.transform.localPosition = low.transform.localPosition = new Vector3(0.4f, 0.25f, 0);
                high.GetComponent<Renderer>().sharedMaterial = low.GetComponent<Renderer>().sharedMaterial = material;
                high.layer = low.layer = 30;
                var group = root.AddComponent<LODGroup>();
                group.SetLODs(new[] { new LOD(0.15f, new[] { high.GetComponent<Renderer>() }),
                    new LOD(0.01f, new[] { low.GetComponent<Renderer>() }) });
                group.fadeMode = crossFade ? LODFadeMode.CrossFade : LODFadeMode.None;
                group.animateCrossFading = false;
                var fadingLods = group.GetLODs();
                for (int i = 0; i < fadingLods.Length; i++)
                {
                    fadingLods[i].fadeTransitionWidth = 0.4f;
                }
                group.SetLODs(fadingLods);
                group.RecalculateBounds();
                foreach (var part in root.GetComponentsInChildren<MeshRenderer>())
                {
                    if ((features & 1) != 0)
                    {
                        var filter = part.GetComponent<MeshFilter>();
                        var mesh = Object.Instantiate(filter.sharedMesh);
                        ownedMeshes.Add(mesh);
                        uint start = mesh.GetIndexStart(0);
                        uint count = mesh.GetIndexCount(0);
                        mesh.lodCount = 2;
                        mesh.SetLod(0, 0, new MeshLodRange(start, count));
                        mesh.SetLod(0, 1, new MeshLodRange(start, count / 6 * 3));
                        filter.sharedMesh = mesh;
                        part.forceMeshLod = 1;
                    }
                    if ((features & 8) != 0)
                    {
                        part.lightmapIndex = 0;
                        part.lightmapScaleOffset = new Vector4(0.5f, 0.5f, 0.25f, 0.25f);
                    }
                }
                if ((features & 256) != 0)
                {
                    groundTable = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4);
                    groundTable.SetData(new uint[] { 1 });
                    groundPages = new Texture2DArray[3];
                    Color[] channels = { new Color(0.2f,0.65f,0.12f,0.35f), new Color(0,0,1,1), new Color(0.02f,0.04f,0,0.1f) };
                    for (int i = 0; i < 3; i++)
                    {
                        groundPages[i] = new Texture2DArray(1,1,1,TextureFormat.RGBAFloat,false,true) { hideFlags = HideFlags.HideAndDontSave };
                        groundPages[i].SetPixels(new[] { channels[i] },0);
                        groundPages[i].Apply();
                    }
                    Shader.SetGlobalBuffer("_LoogaGroundCacheTable", groundTable);
                    Shader.SetGlobalTexture("_LoogaGroundCacheAlbedo", groundPages[0]);
                    Shader.SetGlobalTexture("_LoogaGroundCacheNormal", groundPages[1]);
                    Shader.SetGlobalTexture("_LoogaGroundCacheEmission", groundPages[2]);
                    var regions = new Vector4[16];
                    var info = new Vector4[16];
                    var valid = new Vector4[16];
                    regions[0] = new Vector4(-16,-2,32,32);
                    info[0] = new Vector4(128,1,0,0);
                    valid[0] = new Vector4(1,0,1,0);
                    Shader.SetGlobalVectorArray("_LoogaGroundCacheRegions", regions);
                    Shader.SetGlobalVector("_LoogaGroundCacheInfo", info[0]);
                    Shader.SetGlobalVectorArray("_LoogaGroundCacheSlots", valid);
                    Shader.SetGlobalInt("_LoogaGroundCacheCount", 1);
                    Shader.EnableKeyword("LOOGA_GROUND_CACHE");
                }
                var prototype = InstancePrototype.FromPrefab(root).WithLegacyLightProbes((features & 2) != 0);
                root.SetActive(false);
                var cameraObject = new GameObject("Camera");
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.scene = scene;
                target.Create();
                camera.cullingMask = 1 << 30;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = (features & 8) != 0 ? new Color(0.2f, 0, 0.4f) : Color.black;
                camera.orthographic = orthographic;
                camera.orthographicSize = 4;
                camera.fieldOfView = 60;
                camera.farClipPlane = 100;
                camera.targetTexture = target;
                camera.transform.position = new Vector3(0, 1, -10);
                var transforms = new[] {
                    Matrix4x4.TRS(new Vector3(-2, 0, 0), Quaternion.Euler(0, 27, 0), new Vector3(-1, 2, 1)),
                    Matrix4x4.TRS(new Vector3(2, 0, 0), Quaternion.Euler(0, 14, 0), Vector3.one),
                    Matrix4x4.TRS(new Vector3(0, 0, 15), Quaternion.identity, Vector3.one),
                    Matrix4x4.TRS(new Vector3(80, 0, 0), Quaternion.identity, Vector3.one) };
                if ((features & 32) != 0)
                {
                    var surfaceObject = new GameObject("Painted mesh source");
                    SceneManager.MoveGameObjectToScene(surfaceObject, scene);
                    var mesh = new Mesh { name = "Scatter validation surface" };
                    ownedMeshes.Add(mesh);
                    mesh.vertices = new[] { new Vector3(-4, 0, -1), new Vector3(-4, 0, 7),
                        new Vector3(4, 0, -1), new Vector3(4, 0, 7) };
                    mesh.triangles = new[] { 0, 1, 2, 2, 1, 3 };
                    mesh.RecalculateNormals(); mesh.RecalculateBounds();
                    var filter = surfaceObject.AddComponent<MeshFilter>();
                    filter.sharedMesh = mesh;
                    var targetObject = new GameObject("Scatter target");
                    SceneManager.MoveGameObjectToScene(targetObject, scene);
                    targetObject.layer = 30;
                    scatterTarget = targetObject.AddComponent<InstanceContainer>();
                    scatterTarget.enabled = false;
                    scatterTarget.Configure(root, null);
                    var author = surfaceObject.AddComponent<MeshScatterAuthoring>();
                    author.enabled = false;
                    author.Configure(filter, new[] { new MeshScatterAuthoring.Target { Container = scatterTarget, Weight = 1 } },
                        new MeshScatterSettings { Density = 0.5f, Scale = Vector2.one * 0.5f });
                    author.Paint(new Vector3(0, 0, 2), 2, -1, 0);
                    scatterPlacements = scatterTarget.CopyPlacements();
                    transforms = Array.ConvertAll(scatterPlacements, placement => Matrix4x4.TRS(placement.Position,
                        Quaternion.Euler(placement.EulerAngles), placement.Scale));
                    Assert.Greater(transforms.Length, 10);
                }
                var natives = new GameObject[transforms.Length];
                for (int i = 0; i < transforms.Length; i++)
                {
                    natives[i] = Object.Instantiate(root);
                    SceneManager.MoveGameObjectToScene(natives[i], scene);
                    Matrix4x4 matrix = transforms[i];
                    natives[i].transform.SetPositionAndRotation(matrix.GetColumn(3), matrix.rotation);
                    // Preserve signed TRS explicitly for the reflected fixture.
                    natives[i].transform.rotation = i == 0 ? Quaternion.Euler(0, 27, 0) : i == 1 ? Quaternion.Euler(0, 14, 0) : Quaternion.identity;
                    natives[i].transform.localScale = i == 0 ? new Vector3(-1, 2, 1) : Vector3.one;
                    if (scatterPlacements != null)
                    {
                        natives[i].transform.SetPositionAndRotation(scatterPlacements[i].Position, Quaternion.Euler(scatterPlacements[i].EulerAngles));
                        natives[i].transform.localScale = scatterPlacements[i].Scale;
                    }
                    if (tint)
                    {
                        var block = new MaterialPropertyBlock();
                        block.SetColor("_BaseColor", material.GetColor("_BaseColor") * new Color(0.4f, 0.7f, 0.5f));
                        foreach (var part in natives[i].GetComponentsInChildren<Renderer>(true))
                        {
                            part.SetPropertyBlock(block);
                        }
                    }
                    if ((features & 8) != 0)
                    {
                        foreach (var part in natives[i].GetComponentsInChildren<Renderer>(true))
                        {
                            part.lightmapIndex = 0;
                            part.lightmapScaleOffset = new Vector4(0.5f, 0.5f, 0.25f, 0.25f);
                        }
                    }
                    natives[i].SetActive(true);
                }
                Capture(camera, target, read);
                yield return null;
                yield return WaitForShaders();
                Color32[] native = Capture(camera, target, read);
                foreach (GameObject item in natives)
                {
                    item.SetActive(false);
                }
                if (scatterTarget != null)
                {
                    scatterTarget.enabled = true;
                    instances = scatterTarget.Renderer;
                    Assert.IsNotNull(instances, scatterTarget.Diagnostic);
                }
                else if ((features & 16) != 0)
                {
                    var containerObject = new GameObject("Moving container");
                    SceneManager.MoveGameObjectToScene(containerObject, scene);
                    containerObject.layer = 30;
                    containerObject.transform.position = Vector3.right * 1000;
                    var container = containerObject.AddComponent<InstanceContainer>();
                    var placements = new InstanceContainer.Placement[natives.Length];
                    for (int i = 0; i < placements.Length; i++)
                    {
                        placements[i] = new InstanceContainer.Placement
                        {
                            Id = "placement-" + i,
                            Position = natives[i].transform.position,
                            EulerAngles = natives[i].transform.eulerAngles,
                            Scale = natives[i].transform.localScale
                        };
                    }
                    container.Configure(root, placements);
                    instances = container.Renderer;
                    var distant = Capture(camera, target, read);
                    Assert.Less(Array.FindAll(distant, pixel => pixel.g > 40).Length, 5);
                    containerObject.transform.position = Vector3.zero;
                    container.Synchronize();
                    Assert.AreSame(instances, container.Renderer);
                }
                else
                {
                    instances = new InstanceRenderer(layer: 30);
                    int registered = instances.Register(prototype);
                    foreach (Matrix4x4 matrix in transforms)
                    {
                        instances.Add(registered, matrix, tint ? new InstanceAppearance(new Color(0.4f, 0.7f, 0.5f), Color.white, Vector4.zero) : InstanceAppearance.Default);
                    }
                    instances.Flush();
                }
                Capture(camera, target, read);
                yield return null;
                yield return WaitForShaders();
                Color32[] indirect = Capture(camera, target, read);
                if (shaderName != null)
                {
                    TestContext.WriteLine("Draw counts: " + string.Join(",", instances.ReadDrawCountsForDiagnostics()));
                }
                if (vegetation || features != 0 || shaderName != null)
                {
                    string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LoogaVegetationParity");
                    System.IO.Directory.CreateDirectory(directory);
                    string name = (shaderName ?? "Default").Replace("/", "-") + "-" + orthographic + "-" + crossFade + "-" + billboard + "-f" + features + "-m" + mode;
                    read.SetPixels32(native);
                    read.Apply();
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, name + "-Native.png"), read.EncodeToPNG());
                    read.SetPixels32(indirect);
                    read.Apply();
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(directory, name + "-Looga.png"), read.EncodeToPNG());
                }
                TestContext.WriteLine("Native pixels=" + Array.FindAll(native, p => p.g > 40).Length + "; Looga pixels=" + Array.FindAll(indirect, p => p.g > 40).Length);
                if ((features & 8) != 0)
                {
                    var lines = new System.Collections.Generic.List<string>();
                    lines.Add("draws=" + string.Join(",", instances.ReadDrawCountsForDiagnostics()));
                    lines.Add("nativeIndex=" + natives[0].GetComponentInChildren<Renderer>(true).lightmapIndex);
                    lines.Add("maps=" + LightmapSettings.lightmaps.Length);
                    foreach (var name in new[] { "unity_Lightmap", "unity_Lightmaps", "unity_LightmapInd", "unity_LightmapsInd" })
                    {
                        var texture = Shader.GetGlobalTexture(name);
                        lines.Add(name + "=" + (texture ? texture.name + " " + texture.width : "null"));
                    }
                    System.IO.File.WriteAllLines(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LoogaLightmapDiagnostic.txt"), lines);
                }
                int covered = 0;
                int mismatched = 0;
                for (int i = 0; i < native.Length; i++)
                {
                    if (native[i].g > 40)
                    {
                        covered++;
                    }
                    if (Math.Abs(native[i].g - indirect[i].g) > 8)
                    {
                        mismatched++;
                    }
                }
                Assert.Greater(covered, 300, "The native control must contain visible meshes.");
                Assert.Less(mismatched, 80, "Native and BRG images differ.");
                foreach (var owner in scene.GetRootGameObjects())
                {
                    if (owner.TryGetComponent<InstanceContainer>(out var container))
                    {
                        container.enabled = false;
                    }
                }
                instances.Dispose();
                yield return null;
                Color32[] empty = Capture(camera, target, read);
                if (shaderName != null)
                {
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LoogaVegetationParity", shaderName.Replace("/", "-") + "-Empty.png"), read.EncodeToPNG());
                }
                Assert.Less(Array.FindAll(empty, pixel => pixel.g > 40).Length, 5, "Disposed batches still render.");
            }
            finally
            {
                if ((features & 256) != 0)
                {
                    Shader.DisableKeyword("LOOGA_GROUND_CACHE");
                    Shader.SetGlobalInt("_LoogaGroundCacheCount", 0);
                }
                instances?.Dispose();
                RenderSettings.ambientMode = oldAmbient;
                RenderSettings.ambientLight = oldAmbientColor;
                RenderSettings.ambientProbe = oldProbe;
                LightmapSettings.lightmaps = oldLightmaps;
                LightmapSettings.lightmapsMode = oldLightmapMode;
                if (lightmap) Object.DestroyImmediate(lightmap);
                GraphicsSettings.defaultRenderPipeline = oldPipeline;
                QualitySettings.renderPipeline = oldQuality;
                ShaderUtil.allowAsyncCompilation = oldAsync;
                if ((features & 8) != 0)
                {
                    foreach (var rootObject in scene.GetRootGameObjects())
                    {
                        if (!existingRoots.Contains(rootObject)) Object.DestroyImmediate(rootObject);
                    }

                    if (ownsLightmapScene)
                    {
                        EditorSceneManager.CloseScene(scene, true);
                        if (previousScene.IsValid() && previousScene.isLoaded)
                        {
                            SceneManager.SetActiveScene(previousScene);
                        }
                    }
                }
                else
                {
                    EditorSceneManager.ClosePreviewScene(scene);
                }
                // URP owns feature disposal. Keep feature resources alive until the restored pipeline takes over.
                _pendingGraphicsCleanup = () =>
                {
                    if (companion)
                    {
                        Object.DestroyImmediate(companion);
                    }
                    if (groundTable != null)
                    {
                        Shader.SetGlobalBuffer("_LoogaGroundCacheTable", (GraphicsBuffer)null);
                        Shader.SetGlobalTexture("_LoogaGroundCacheAlbedo", null);
                        Shader.SetGlobalTexture("_LoogaGroundCacheNormal", null);
                        Shader.SetGlobalTexture("_LoogaGroundCacheEmission", null);
                        groundTable.Dispose();
                    }
                    if (groundPages != null)
                    {
                        foreach (var page in groundPages) Object.DestroyImmediate(page);
                    }
                    if (groundData)
                    {
                        Object.DestroyImmediate(groundData);
                    }
                    foreach (var mesh in ownedMeshes)
                    {
                        Object.DestroyImmediate(mesh);
                    }
                    foreach (Object resource in new Object[] { pipeline, rendererData, material, target, read })
                    {
                        Object.DestroyImmediate(resource);
                    }
                };
            }
        }

        private static IEnumerator WaitForShaders()
        {
            double deadline = EditorApplication.timeSinceStartup + 30;
            while (ShaderUtil.anythingCompiling && EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
            }
            Assert.IsFalse(ShaderUtil.anythingCompiling, "Shader compilation exceeded the image-test deadline.");
        }

        private static Color32[] Capture(Camera camera, RenderTexture target, Texture2D read)
        {
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            var previous = RenderTexture.active;
            RenderTexture.active = target;
            read.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            read.Apply();
            RenderTexture.active = previous;
            return read.GetPixels32();
        }
    }
}
