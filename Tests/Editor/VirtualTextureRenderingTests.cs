using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class VirtualTextureRenderingTests
    {
        [Test]
        public void PendingFeedbackRetainsTexturesUntilTheRequestCompletes()
        {
            var type = typeof(TerrainVirtualTexture).Assembly.GetType("LoogaSoft.Terrain.VirtualSurfaceCache")
                .GetNestedType("Feedback", System.Reflection.BindingFlags.NonPublic);
            object feedback = System.Activator.CreateInstance(type, true);
            var color = new RenderTexture(16, 16, 0);
            var depth = new RenderTexture(16, 16, 24, RenderTextureFormat.Depth);
            color.Create();
            depth.Create();
            type.GetField("Color").SetValue(feedback, color);
            type.GetField("Depth").SetValue(feedback, depth);
            try
            {
                type.GetField("Pending").SetValue(feedback, true);
                type.GetMethod("Dispose").Invoke(feedback, null);
                Assert.That(color.IsCreated() && depth.IsCreated(), Is.True);
                type.GetField("Pending").SetValue(feedback, false);
                type.GetMethod("Dispose").Invoke(feedback, null);
                Assert.That(!color && !depth, Is.True);
            }
            finally
            {
                if (color) Object.DestroyImmediate(color);
                if (depth) Object.DestroyImmediate(depth);
            }
        }

        [TestCase(false, false, false)]
        [TestCase(false, false, true)]
        [TestCase(false, true, false)]
        [TestCase(false, true, true)]
        [TestCase(true, false, false)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        [TestCase(true, true, true)]
        public void MultiplePagesPreserveColorThroughEvictionEditsAndDisable(bool compressed, bool parallel, bool compact)
        {
            if (!SystemInfo.supportsAsyncGPUReadback) Assert.Ignore("GPU readback is required.");
            Scene scene = EditorSceneManager.NewPreviewScene();
            RenderPipelineAsset previous = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset previousQuality = QualitySettings.renderPipeline;
            string previousPath = AssetDatabase.GetAssetPath(previous);
            string previousQualityPath = AssetDatabase.GetAssetPath(previousQuality);
            bool asynchronous = ShaderUtil.allowAsyncCompilation;
            var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            var pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 40, 32) };
            var material = new Material(Shader.Find("Looga/Terrain/Lit"));
            var settings = ScriptableObject.CreateInstance<VirtualTextureSettings>();
            var target = new RenderTexture(128, 128, 24);
            var image = new Texture2D(128, 128, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                // Preview pipeline changes can unload assets not retained by scene objects.
                foreach (Object resource in new Object[] { rendererData, pipeline, data, material, settings, target, image })
                {
                    resource.hideFlags = HideFlags.HideAndDontSave;
                }
                ShaderUtil.allowAsyncCompilation = false;
                GraphicsSettings.defaultRenderPipeline = QualitySettings.renderPipeline = pipeline;
                var heights = new float[33, 33];
                for (int z = 0; z < 33; z++)
                {
                    for (int x = 0; x < 33; x++) heights[z, x] = 0.1f;
                }
                data.SetHeights(0, 0, heights);
                GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(data);
                tile.layer = 30;
                SceneManager.MoveGameObjectToScene(tile, scene);
                var cameraObject = new GameObject("Virtual texture test camera");
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.scene = scene;
                camera.cullingMask = 1 << 30;
                camera.orthographic = true;
                camera.orthographicSize = 3;
                camera.transform.SetPositionAndRotation(new Vector3(16, 60, 16), Quaternion.Euler(90, 0, 0));
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.magenta;
                target.Create();
                camera.targetTexture = target;
                material.SetColor("_BaseColor", new Color(0.3f, 0.6f, 0.1f));
                TerrainRenderer renderer = tile.AddComponent<TerrainRenderer>();
                renderer.Configure(AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/TerrainSelection.compute"), material, 32);
                Draw(camera, target, 4);
                Color direct = Read(target, image);
                Assert.That(direct.g, Is.GreaterThan(0.05f), "The direct surface must be visible.");

                settings.Configure(Shader.Find("Hidden/Looga/Terrain/VirtualTextureFeedback"), 1024, 16, 4, 1);
                settings.ConfigureOptimizations(AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/VirtualTexturing/VirtualTextureCompression.compute"), compressed, parallel);
                if (compact)
                {
                    settings.ConfigureFeedbackCompaction(AssetDatabase.LoadAssetAtPath<ComputeShader>("Packages/com.loogasoft.loogaterrain/Runtime/Shaders/VirtualTexturing/VirtualTextureCompact.compute"));
                }
                TerrainVirtualTexture virtualTexture = tile.AddComponent<TerrainVirtualTexture>();
                var mismatchedProducer = new Material(material);
                try
                {
                    mismatchedProducer.SetOverrideTag(TerrainVirtualTexture.SchemaTag, "Other schema");
                    UnityEngine.TestTools.LogAssert.Expect(LogType.Warning,
                        "Looga virtual texture: The surface and page producer schemas differ. Re-export and assign the matching pair.");
                    virtualTexture.Configure(settings, mismatchedProducer);
                    Assert.That(virtualTexture.Diagnostic, Does.Contain("schemas differ"));
                    Assert.That(virtualTexture.Statistics.Capacity, Is.Zero);
                }
                finally
                {
                    Object.DestroyImmediate(mismatchedProducer);
                }
                virtualTexture.Configure(settings, material);
                Draw(camera, target, 12);
                Assert.That(virtualTexture.Statistics.ResidentPages, Is.GreaterThan(4), "Exercise multiple physical slices.");
                Assert.That(virtualTexture.Statistics.Compressed, Is.EqualTo(compressed));
                Assert.That(virtualTexture.Statistics.CacheBytes, Is.EqualTo(136L * 136 * 16 * (compressed ? 10 : 20)));
                Match(direct, Read(target, image));

                camera.transform.position = new Vector3(4, 60, 4);
                Draw(camera, target, 12);
                Assert.That(virtualTexture.Statistics.Evictions, Is.GreaterThan(0));
                Assert.That(virtualTexture.Statistics.ResidentPages, Is.LessThanOrEqualTo(16));
                Color movedCache = Read(target, image);
                virtualTexture.enabled = false;
                Draw(camera, target, 4);
                // Compare the same view. Reflections can change with camera position.
                Match(Read(target, image), movedCache);
                virtualTexture.enabled = true;
                Draw(camera, target, 12);

                long fills = virtualTexture.Statistics.PageFills;
                int geometryRevision = renderer.GeometryRevision;
                material.SetColor("_BaseColor", new Color(0.7f, 0.1f, 0.05f));
                renderer.RefreshSurface();
                Draw(camera, target, 1);
                Color changedDirect = Read(target, image);
                Draw(camera, target, 12);
                Match(changedDirect, Read(target, image));
                Assert.That(virtualTexture.Statistics.PageFills, Is.GreaterThan(fills));
                Assert.That(renderer.GeometryRevision, Is.EqualTo(geometryRevision));

                virtualTexture.enabled = false;
                Draw(camera, target, 4);
                Match(changedDirect, Read(target, image));
                int stableRevision = renderer.SurfaceRevision;
                Color liveColor = new Color(0.1f, 0.3f, 0.8f);
                System.Action<MaterialPropertyBlock> liveBinding = block => block.SetColor("_BaseColor", liveColor);
                renderer.BindDynamicMaterialProperties += liveBinding;
                Draw(camera, target, 4);
                Color blue = Read(target, image);
                Assert.That(blue.b, Is.GreaterThan(blue.r));
                liveColor = new Color(0.8f, 0.1f, 0.1f);
                Draw(camera, target, 4);
                Color red = Read(target, image);
                Assert.That(red.r, Is.GreaterThan(red.b));
                Assert.That(renderer.SurfaceRevision, Is.EqualTo(stableRevision));
                Assert.That(renderer.GeometryRevision, Is.EqualTo(geometryRevision));
                renderer.BindDynamicMaterialProperties -= liveBinding;
                renderer.RefreshSurface();
                virtualTexture.enabled = true;
                settings.Configure(settings.FeedbackShader, 2048, 32, 4, 1);
                Draw(camera, target, 16);
                Assert.That(virtualTexture.Statistics.Capacity, Is.EqualTo(32));
                Match(changedDirect, Read(target, image));

                settings.Configure(settings.FeedbackShader, 512, 16, 4, 1);
                Draw(camera, target, 8);
                Assert.That(virtualTexture.Statistics.PageFills, Is.Zero, "Close detail must use direct shading.");
                Match(changedDirect, Read(target, image));

                data.SetHoles(3, 3, new bool[3, 3]);
                Draw(camera, target, 4);
                Match(Color.magenta, Read(target, image));
                Assert.That(tile.GetComponent<TerrainCollider>().terrainData, Is.SameAs(data));
            }
            finally
            {
                GraphicsSettings.defaultRenderPipeline = string.IsNullOrEmpty(previousPath)
                    ? previous : AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(previousPath);
                QualitySettings.renderPipeline = string.IsNullOrEmpty(previousQualityPath)
                    ? previousQuality : AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(previousQualityPath);
                ShaderUtil.allowAsyncCompilation = asynchronous;
                EditorSceneManager.ClosePreviewScene(scene);
                target.Release();
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(image);
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(pipeline);
                Object.DestroyImmediate(rendererData);
            }
        }

        private static void Draw(Camera camera, RenderTexture target, int frames)
        {
            for (int frame = 0; frame < frames; frame++)
            {
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                // Blocking completion is limited to this regression test.
                AsyncGPUReadback.WaitAllRequests();
                // A tight synchronous test loop must explicitly join workers between simulated frames.
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                foreach (TerrainVirtualTexture component in Resources.FindObjectsOfTypeAll<TerrainVirtualTexture>())
                {
                    object cache = typeof(TerrainVirtualTexture).GetField("_cache", flags).GetValue(component);
                    if (cache == null) continue;
                    var feedback = (System.Collections.IDictionary)cache.GetType().GetField("_feedback", flags).GetValue(cache);
                    foreach (object state in feedback.Values)
                    {
                        var analysis = (VirtualTextureFeedbackBatch)state.GetType().GetField("Analysis").GetValue(state);
                        analysis.WaitForCompletion();
                    }
                }
            }
        }

        private static Color Read(RenderTexture target, Texture2D image)
        {
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, 128, 128), 0, 0);
                image.Apply();
                return image.GetPixel(64, 64);
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        private static void Match(Color expected, Color actual)
        {
            Assert.That(Vector4.Distance(expected, actual), Is.LessThan(0.04f));
        }
    }
}
