using System;
using System.Reflection;
using LoogaSoft.Instancing.Universal;
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
    public sealed class TerrainVisibilityTests
    {
        private const string Package = "Packages/com.loogasoft.loogaterrain/Runtime/";

        [TestCase(false, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(true, true, true)]
        [TestCase(false, true, false)]
        [TestCase(true, true, false)]
        public void OcclusionPreservesPixelsAcrossMotionAndLocalizedHeightChanges(bool orthographic, bool batch, bool depth)
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            string previous = AssetDatabase.GetAssetPath(GraphicsSettings.defaultRenderPipeline);
            string quality = AssetDatabase.GetAssetPath(QualitySettings.renderPipeline);
            bool asynchronous = ShaderUtil.allowAsyncCompilation;
            var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            var depthFeature = ScriptableObject.CreateInstance<InstanceOcclusionRendererFeature>();
            // This test validates culling correctness with a small fixture. Adaptive capture
            // intentionally disables Hi-Z below the production break-even population.
            depthFeature.ConfigureAdaptive(false);
            depthFeature.SetActive(true);
            rendererData.rendererFeatures.Add(depthFeature);
            depthFeature.Create();
            var pipeline = UniversalRenderPipelineAsset.Create(rendererData);
            var data = new TerrainData { heightmapResolution = 257, size = new Vector3(256, 100, 256) };
            var material = new Material(Shader.Find("Looga/Terrain/Lit"));
            var target = new RenderTexture(321, 237, 24);
            var read = new Texture2D(321, 237, TextureFormat.RGBA32, false);
            try
            {
                foreach (Object resource in new Object[] { rendererData, depthFeature, pipeline, data, material, target, read })
                {
                    resource.hideFlags = HideFlags.HideAndDontSave;
                }
                GraphicsSettings.defaultRenderPipeline = QualitySettings.renderPipeline = pipeline;
                ShaderUtil.allowAsyncCompilation = false;
                var heights = new float[257, 257];
                for (int y = 0; y < 257; y++)
                {
                    for (int x = 0; x < 257; x++)
                    {
                        heights[y, x] = 0.1f + 0.65f * Mathf.Exp(-Mathf.Pow((y - 85f) / 16f, 2))
                            + 0.06f * Mathf.Sin(x * 0.17f) * Mathf.Sin(y * 0.09f);
                    }
                }
                data.SetHeights(0, 0, heights);
                GameObject tile = UnityEngine.Terrain.CreateTerrainGameObject(data);
                tile.layer = 30;
                SceneManager.MoveGameObjectToScene(tile, scene);
                var renderer = tile.AddComponent<TerrainRenderer>();
                renderer.Configure(AssetDatabase.LoadAssetAtPath<ComputeShader>(Package + "Shaders/TerrainSelection.compute"), material, 64);
                renderer.SetLodQuality(0.25f);
                renderer.SetSurfaceBatching(true, 2);
                GameObject adjacent = UnityEngine.Terrain.CreateTerrainGameObject(data);
                adjacent.layer = 30;
                adjacent.transform.position = new Vector3(256, 0, 0);
                SceneManager.MoveGameObjectToScene(adjacent, scene);
                var second = adjacent.AddComponent<TerrainRenderer>();
                second.Configure(AssetDatabase.LoadAssetAtPath<ComputeShader>(Package + "Shaders/TerrainSelection.compute"), material, 64);
                second.SetLodQuality(0.25f);
                second.SetSurfaceBatching(true, 2);
                var sunObject = new GameObject("Visibility test sun");
                SceneManager.MoveGameObjectToScene(sunObject, scene);
                var sun = sunObject.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.shadows = LightShadows.Hard;
                sun.transform.rotation = Quaternion.Euler(25, 110, 0);
                var cameraObject = new GameObject("Visibility test camera");
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.scene = scene;
                camera.cullingMask = 1 << 30;
                camera.orthographic = orthographic;
                camera.orthographicSize = 70;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 700;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.magenta;
                target.Create();
                camera.targetTexture = target;
                var occlusion = cameraObject.AddComponent<TerrainOcclusionCamera>();
                occlusion.Configure(AssetDatabase.LoadAssetAtPath<Shader>(Package + "Shaders/TerrainOcclusionDepth.shader"), batch ? AssetDatabase.LoadAssetAtPath<Shader>(Package + "Shaders/TerrainBatchedShadows.shader") : null, true, depth);
                for (int view = 0; view < 5; view++)
                {
                    if (view == 4)
                    {
                        data.SetHoles(120, 80, new bool[16, 16]);
                    }
                    camera.transform.SetPositionAndRotation(new Vector3(100 + view * 8, 24 + view * 8, 10 + view * 11), Quaternion.Euler(view * 3, view * 5, 0));
                    occlusion.enabled = false;
                    Draw(camera, target);
                    Color32[] baseline = Pixels(target, read);
                    if (view == 0)
                    {
                        int submissions = renderer.GetLastSubmissionCount(camera) + second.GetLastSubmissionCount(camera);
                        Assert.That(submissions, Is.InRange(1, 3), "Two compatible surfaces should share one surface submission.");
                    }
                    uint before = Count(renderer, camera);
                    occlusion.enabled = true;
                    Draw(camera, target);
                    Draw(camera, target);
                    Color32[] actual = Pixels(target, read);
                    uint after = Count(renderer, camera);
                    Assert.That(after, Is.LessThanOrEqualTo(before));
                    if (depth)
                    {
                        if (view == 0)
                        {
                            Assert.That(occlusion.DepthSize.x, Is.GreaterThan(0), "The stable view must consume shared depth.");
                            Assert.That(occlusion.DepthSize.y, Is.GreaterThan(0), "The stable view must consume shared depth.");
                        }
                        else
                        {
                            // SubmitRenderRequest does not advance Time.frameCount. The camera moves farther
                            // than the two-meter history limit, so the prior capture must fail visible until
                            // the next frame can publish a new pyramid.
                            Assert.That(occlusion.DepthSize, Is.EqualTo(Vector2Int.zero));
                        }
                    }
                    double error = 0;
                    for (int index = 0; index < actual.Length; index++)
                    {
                        error += Math.Abs(actual[index].r - baseline[index].r)
                            + Math.Abs(actual[index].g - baseline[index].g) + Math.Abs(actual[index].b - baseline[index].b);
                    }
                    Assert.That(error / (actual.Length * 3), Is.LessThan(0.05), "View " + view);
                }
                data.SetHeights(128, 128, new float[,] { { 0.9f, 0.85f }, { 0.85f, 0.9f } });
                Draw(camera, target);
                Assert.That(renderer.LastMetadataNodeCount, Is.LessThan(5461));
                var field = typeof(TerrainRenderer).GetField("_nodeData", BindingFlags.Instance | BindingFlags.NonPublic);
                var buffer = (GraphicsBuffer)field.GetValue(renderer);
                var partial = new Vector4[buffer.count];
                buffer.GetData(partial);
                renderer.RefreshSource();
                Draw(camera, target);
                var full = new Vector4[buffer.count];
                buffer.GetData(full);
                CollectionAssert.AreEqual(full, partial, "Localized metadata must equal a complete rebuild.");
                Assert.That(renderer.LastMetadataNodeCount, Is.EqualTo(5461));
                occlusion.enabled = false;
                Assert.That(occlusion.DepthSize, Is.EqualTo(Vector2Int.zero));
            }
            finally
            {
                AsyncGPUReadback.WaitAllRequests();
                EditorSceneManager.ClosePreviewScene(scene);
                GraphicsSettings.defaultRenderPipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(previous);
                QualitySettings.renderPipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(quality);
                ShaderUtil.allowAsyncCompilation = asynchronous;
                foreach (Object resource in new Object[] { rendererData, depthFeature, pipeline, data, material, target, read })
                {
                    Object.DestroyImmediate(resource);
                }
            }
        }

        private static void Draw(Camera camera, RenderTexture target)
        {
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            AsyncGPUReadback.WaitAllRequests();
        }

        private static Color32[] Pixels(RenderTexture target, Texture2D read)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            read.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            read.Apply();
            RenderTexture.active = previous;
            return read.GetPixels32();
        }

        private static uint Count(TerrainRenderer renderer, Camera camera)
        {
            uint result = 0;
            renderer.RequestPatchCounts(camera, (visible, shadows) => result = visible);
            AsyncGPUReadback.WaitAllRequests();
            return result;
        }
    }
}
