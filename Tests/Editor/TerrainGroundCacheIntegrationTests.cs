using System.Collections;
using LoogaSoft.Terrain;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class TerrainGroundCacheIntegrationTests
    {
        [UnityTest]
        public IEnumerator ReceiverUsesExistingPagesAndReleasesBorrowedBindings([Values(false, true)] bool microSplat)
        {
            var surfaceAsset = microSplat ? AssetDatabase.LoadAssetAtPath<Material>("Assets/Tests/LoogaTerrain/MicroSplatFull/WorldSurface.mat") : null;
            var producerAsset = microSplat ? AssetDatabase.LoadAssetAtPath<Material>("Assets/Tests/LoogaTerrain/MicroSplatFull/WorldSurfacePageProducer.mat") : null;
            if (microSplat && (!surfaceAsset || !producerAsset)) Assert.Ignore("The optional consumer MicroSplat fixture is absent.");
            Assert.Zero(Shader.GetGlobalInt("_LoogaGroundCacheCount"));
            if (!microSplat)
            {
                AssetDatabase.ImportAsset("Packages/com.loogasoft.loogaterrain/Runtime/Shaders/TerrainLit.shader", ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                yield return null;
            }
            Scene scene = EditorSceneManager.NewPreviewScene();
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 8, 32) };
            var layer = new TerrainLayer { diffuseTexture = Texture2D.whiteTexture };
            data.terrainLayers = new[] { layer };
            var material = surfaceAsset ? new Material(surfaceAsset) : new Material(Shader.Find("Looga/Terrain/Lit"));
            var producer = producerAsset ? new Material(producerAsset) : material;
            var settings = ScriptableObject.CreateInstance<VirtualTextureSettings>();
            var target = new RenderTexture(128, 128, 24);
            var probe = new Material(Shader.Find("Hidden/Looga/Ground Cache Probe"));
            var sample = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGBFloat);
            bool asynchronous = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                if (!microSplat) material.SetColor("_BaseColor", new Color(0.2f, 0.6f, 0.1f));
                var tile = UnityEngine.Terrain.CreateTerrainGameObject(data);
                SceneManager.MoveGameObjectToScene(tile, scene);
                tile.layer = 30;
                tile.GetComponent<UnityEngine.Terrain>().allowAutoConnect = false;
                var renderer = tile.AddComponent<TerrainRenderer>();
                renderer.Configure(AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    "Packages/com.loogasoft.loogaterrain/Runtime/Shaders/TerrainSelection.compute"), material, 32);
                Assert.IsTrue(renderer.IsReady, renderer.Diagnostic);
                settings.Configure(Shader.Find("Hidden/Looga/Terrain/VirtualTextureFeedback"), 512, 32, 16, 1);
                settings.ConfigureOptimizations(null, false, false);
                var source = tile.AddComponent<TerrainVirtualTexture>();
                source.Configure(settings, producer);
                var bindings = tile.AddComponent<TerrainGroundCacheBindings>();
                bindings.Configure(new[] { source });
                var cameraRoot = new GameObject("Ground cache qualification camera");
                SceneManager.MoveGameObjectToScene(cameraRoot, scene);
                var camera = cameraRoot.AddComponent<Camera>();
                camera.enabled = false;
                camera.scene = scene;
                camera.cullingMask = 1 << 30;
                camera.orthographic = true;
                camera.orthographicSize = 17;
                camera.transform.SetPositionAndRotation(new Vector3(16, 60, 16), Quaternion.Euler(90, 0, 0));
                target.Create();
                sample.Create();
                camera.targetTexture = target;
                probe.SetVector("_ProbeWorld", new Vector4(16, 16, 0, 0));
                for (int i = 0; i < 12; i++)
                {
                    RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                    AsyncGPUReadback.WaitAllRequests();
                    yield return null;
                }
                bindings.Publish();
                Assert.IsNull(bindings.Diagnostic);
                Assert.Greater(source.Statistics.ResidentPages, 0, source.Diagnostic);
                Assert.AreEqual(1, Shader.GetGlobalInt("_LoogaGroundCacheCount"));
                Texture atlas = Shader.GetGlobalTexture("_LoogaGroundCacheAlbedo");
                Assert.AreEqual(136, atlas.width);
                Assert.IsTrue(source.TryGetGroundCache(out object identity, out int entries, out long revision));
                var atlasRead = AsyncGPUReadback.Request(atlas, 0, TextureFormat.RGBAFloat);
                atlasRead.WaitForCompletion();
                TestContext.WriteLine("Raw page pixel=" + atlasRead.GetData<Color>()[136 * 68 + 68]);
                Color albedo = Read(probe, sample, 0);
                Color normal = Read(probe, sample, 1);
                Color emission = Read(probe, sample, 2);
                TestContext.WriteLine("MicroSplat=" + microSplat + "; pages=" + source.Statistics.ResidentPages +
                    "; albedo=" + albedo + "; normal=" + normal + "; emission=" + emission);
                Assert.That(albedo, Is.Not.EqualTo(Color.magenta));
                Assert.Greater(albedo.maxColorComponent, 0.01f);
                Assert.Greater(microSplat ? normal.b : normal.g, 0.1f);
                Assert.AreEqual(!microSplat, source.GroundNormalsAreWorldSpace);
                Assert.IsTrue(float.IsFinite(emission.r) && float.IsFinite(emission.a));
                if (!microSplat)
                {
                    Assert.That(albedo.g, Is.EqualTo(QualitySettings.activeColorSpace == ColorSpace.Linear ? Mathf.GammaToLinearSpace(0.6f) : 0.6f).Within(0.02f));
                }
                long fills = source.Statistics.PageFills;
                bindings.Publish();
                Assert.AreEqual(fills, source.Statistics.PageFills, "Receiver publication must not generate another cache.");
                source.Invalidate();
                Assert.Zero(Shader.GetGlobalInt("_LoogaGroundCacheCount"));
                bindings.Publish();
                Assert.AreSame(atlas, Shader.GetGlobalTexture("_LoogaGroundCacheAlbedo"));
                Assert.AreEqual(Color.magenta, Read(probe, sample, 0), "Invalidated pages must use the direct fallback.");
                Assert.IsTrue(source.TryGetGroundCache(out object retained, out int retainedEntries, out long changed));
                Assert.AreSame(identity, retained);
                Assert.AreEqual(entries, retainedEntries);
                Assert.AreNotEqual(revision, changed);
                source.enabled = false;
                Assert.Zero(Shader.GetGlobalInt("_LoogaGroundCacheCount"));
                bindings.enabled = false;
                Assert.IsNull(Shader.GetGlobalTexture("_LoogaGroundCacheAlbedo"));
                Assert.IsFalse(Shader.IsKeywordEnabled("LOOGA_GROUND_CACHE"));
            }
            finally
            {
                AsyncGPUReadback.WaitAllRequests();
                EditorSceneManager.ClosePreviewScene(scene);
                ShaderUtil.allowAsyncCompilation = asynchronous;
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(layer);
                if (producer != material) Object.DestroyImmediate(producer);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(settings);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(sample);
                Object.DestroyImmediate(probe);
            }
        }

        private static Color Read(Material probe, RenderTexture sample, int channel)
        {
            probe.SetInt("_ProbeChannel", channel);
            ShaderUtil.CompilePass(probe, 0, false);
            using var command = new CommandBuffer();
            ShaderUtil.SetAsyncCompilation(command, false);
            command.SetRenderTarget(sample);
            command.DrawProcedural(Matrix4x4.identity, probe, 0, MeshTopology.Triangles, 3);
            ShaderUtil.RestoreAsyncCompilation(command);
            Graphics.ExecuteCommandBuffer(command);
            var readback = AsyncGPUReadback.Request(sample, 0, TextureFormat.RGBAFloat);
            readback.WaitForCompletion();
            Assert.IsFalse(readback.hasError);
            return readback.GetData<Color>()[5];
        }
    }
}
