using LoogaSoft.Instancing;
using System;
using System.Collections;
using LoogaSoft.Terrain.Instances;
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
    public sealed class GroundReceiverValidation
    {
        [UnityTest]
        public IEnumerator CacheMatchesNativeInExistingPipeline(
            [Values("Looga/Terrain/Vegetation Lit", "LoogaSoft/Grass", "LoogaSoft/Foliage")] string shaderName,
            [Values(false, true)] bool coarseOnly)
        {
            Shader shader = Shader.Find(shaderName);
            if (!shader) Assert.Ignore("The optional material package is absent.");
            Assert.Zero(Shader.GetGlobalInt("_LoogaGroundCount"));
            Assert.Zero(Shader.GetGlobalInt("_LoogaRvtEnabled"));
            bool graphics = shaderName != "Looga/Terrain/Vegetation Lit";
            Scene scene = EditorSceneManager.NewPreviewScene();
            var material = new Material(shader);
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 4, 32) };
            var table = new GraphicsBuffer(GraphicsBuffer.Target.Structured, coarseOnly ? 5 : 1, 4);
            var pages = new Texture2DArray[3];
            var atlases = new Texture2D[3];
            var target = new RenderTexture(64, 64, 24);
            var read = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            InstanceRenderer instances = null;
            RenderTexture previous = RenderTexture.active;
            bool asynchronous = ShaderUtil.allowAsyncCompilation;
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                material.SetColor("_BaseColor", Color.red);
                if (graphics)
                {
                    material.SetVector("_RvtGroundParams", new Vector4(1, 0.5f, 5, 0));
                }
                else
                {
                    material.SetVector("_GroundParams", new Vector4(1, 0.5f, 0, 5));
                    material.SetFloat("_WindTime", 1.7f);
                }
                var terrainObject = new GameObject("Ground receiver fixture");
                SceneManager.MoveGameObjectToScene(terrainObject, scene);
                terrainObject.transform.position = new Vector3(-16, 0, -16);
                var terrain = terrainObject.AddComponent<UnityEngine.Terrain>();
                terrain.terrainData = data;
                terrain.allowAutoConnect = false;
                terrain.drawHeightmap = false;
                var binding = terrainObject.AddComponent<TerrainGroundBindings>();
                binding.Configure(new[] { new TerrainGroundBindings.Surface { Terrain = terrain, Tint = Color.red } });
                binding.Publish();
                var addresses = new uint[coarseOnly ? 5 : 1];
                addresses[addresses.Length - 1] = 1;
                table.SetData(addresses);
                Color[] channels = { new Color(0.2f, 0.6f, 0.1f, 0.35f), new Color(0, 0, 1, 1), new Color(0.03f, 0.1f, 0, 0.1f) };
                for (int i = 0; i < 3; i++)
                {
                    pages[i] = new Texture2DArray(1, 1, 1, TextureFormat.RGBAFloat, false, true);
                    pages[i].SetPixels(new[] { channels[i] }, 0);
                    pages[i].Apply();
                    atlases[i] = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true) { filterMode = FilterMode.Point };
                    var pixels = new Color[16];
                    for (int pixel = 0; pixel < pixels.Length; pixel++)
                    {
                        pixels[pixel] = i == 0 ? new Color(0.2f, 0.6f, 0.1f, 1) :
                            i == 1 ? new Color(0.5f, 0.5f, 0.35f, 0.1f) : new Color(0, 0, 1, 1);
                        if (coarseOnly && i == 0 && pixel % 4 < 2 && pixel / 4 < 2)
                        {
                            pixels[pixel].a = 0;
                        }
                    }
                    atlases[i].SetPixels(pixels);
                    atlases[i].Apply();
                }
                var regions = new Vector4[16];
                var slots = new Vector4[16];
                regions[0] = new Vector4(-16, -16, 32, 32);
                slots[0] = new Vector4(1, 0, addresses.Length, 0);
                Shader.SetGlobalVectorArray("_LoogaGroundCacheRegions", regions);
                Shader.SetGlobalVectorArray("_LoogaGroundCacheSlots", slots);
                Shader.SetGlobalVector("_LoogaGroundCacheInfo", coarseOnly ? new Vector4(256, 2, 1, 0) : new Vector4(128, 1, 0, 0));
                Shader.SetGlobalBuffer("_LoogaGroundCacheTable", table);
                Shader.SetGlobalTexture("_LoogaGroundCacheAlbedo", pages[0]);
                Shader.SetGlobalTexture("_LoogaGroundCacheNormal", pages[1]);
                Shader.SetGlobalTexture("_LoogaGroundCacheEmission", pages[2]);
                Shader.SetGlobalInt("_LoogaGroundCacheCount", 1);
                Shader.EnableKeyword("LOOGA_GROUND_CACHE");
                Shader.SetGlobalTexture("_LoogaRvtAlbedoAtlas", atlases[0]);
                Shader.SetGlobalTexture("_LoogaRvtNormalAtlas", atlases[1]);
                Shader.SetGlobalTexture("_LoogaRvtHeightAtlas", atlases[2]);
                Shader.SetGlobalVectorArray("_LoogaRvtCenterExtents", new[] { new Vector4(0, 0, 16, 1), new Vector4(0, 0, 32, 1), Vector4.zero, Vector4.zero });
                Shader.SetGlobalVectorArray("_LoogaRvtAtlasRects", new[] { new Vector4(0, 0, 0.5f, 0.5f), new Vector4(0.5f, 0, 0.5f, 0.5f), Vector4.zero, Vector4.zero });
                Shader.SetGlobalVector("_LoogaRvtHeightRange", new Vector4(0, 8, 0, 0.1f));
                Shader.SetGlobalInteger("_LoogaRvtClipmapCount", 2);
                Shader.SetGlobalInt("_LoogaRvtEnabled", graphics ? 1 : 0);
                var root = GameObject.CreatePrimitive(PrimitiveType.Cube);
                SceneManager.MoveGameObjectToScene(root, scene);
                root.layer = 30;
                root.transform.position = new Vector3(0, 1, 0);
                root.GetComponent<MeshRenderer>().sharedMaterial = material;
                var sunRoot = new GameObject("Receiver fixture light");
                SceneManager.MoveGameObjectToScene(sunRoot, scene);
                var sun = sunRoot.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.intensity = 2;
                sun.cullingMask = 1 << 30;
                sun.transform.rotation = Quaternion.Euler(35, -20, 0);
                var cameraObject = new GameObject("Receiver fixture camera");
                SceneManager.MoveGameObjectToScene(cameraObject, scene);
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.scene = scene;
                camera.cullingMask = 1 << 30;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.transform.position = new Vector3(0, 2, -5);
                camera.transform.LookAt(new Vector3(0, 1, 0));
                camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;
                target.Create();
                Capture(camera, target, read);
                yield return null;
                Color32[] native = Capture(camera, target, read);
                var prototype = InstancePrototype.FromPrefab(root);
                root.SetActive(false);
                instances = new InstanceRenderer(layer: 30);
                int id = instances.Register(prototype);
                instances.Add(id, Matrix4x4.TRS(new Vector3(0, 1, 0), Quaternion.identity, Vector3.one));
                instances.Flush();
                Capture(camera, target, read);
                yield return null;
                Color32[] looga = Capture(camera, target, read);
                int nativePixels = Array.FindAll(native, pixel => pixel.g > 20).Length;
                int count = Array.FindAll(looga, pixel => pixel.g > 20).Length;
                int mismatch = 0;
                for (int i = 0; i < looga.Length; i++)
                {
                    if (Math.Abs(looga[i].g - native[i].g) > 8)
                    {
                        mismatch++;
                    }
                }
                TestContext.WriteLine("Native=" + nativePixels + " Looga=" + count + " mismatched=" + mismatch +
                    " draws=" + string.Join(",", instances.ReadDrawCountsForDiagnostics()));
                Assert.Greater(nativePixels, 20, "The red material must receive visible green cache data.");
                Assert.Greater(count, 20);
                Assert.LessOrEqual(mismatch, 5);
            }
            finally
            {
                Shader.DisableKeyword("LOOGA_GROUND_CACHE");
                Shader.SetGlobalInt("_LoogaGroundCacheCount", 0);
                Shader.SetGlobalBuffer("_LoogaGroundCacheTable", (GraphicsBuffer)null);
                Shader.SetGlobalTexture("_LoogaGroundCacheAlbedo", null);
                Shader.SetGlobalTexture("_LoogaGroundCacheNormal", null);
                Shader.SetGlobalTexture("_LoogaGroundCacheEmission", null);
                Shader.SetGlobalInt("_LoogaRvtEnabled", 0);
                Shader.SetGlobalInteger("_LoogaRvtClipmapCount", 0);
                Shader.SetGlobalTexture("_LoogaRvtAlbedoAtlas", null);
                Shader.SetGlobalTexture("_LoogaRvtNormalAtlas", null);
                Shader.SetGlobalTexture("_LoogaRvtHeightAtlas", null);
                instances?.Dispose();
                RenderTexture.active = previous;
                ShaderUtil.allowAsyncCompilation = asynchronous;
                EditorSceneManager.ClosePreviewScene(scene);
                table.Dispose();
                foreach (var page in pages)
                {
                    if (page) Object.DestroyImmediate(page);
                }
                foreach (var atlas in atlases)
                {
                    if (atlas) Object.DestroyImmediate(atlas);
                }
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(read);
            }
        }

        private static Color32[] Capture(Camera camera, RenderTexture target, Texture2D read)
        {
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target;
            read.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            read.Apply();
            return read.GetPixels32();
        }
    }
}
