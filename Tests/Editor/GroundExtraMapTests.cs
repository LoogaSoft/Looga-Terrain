using LoogaSoft.Instancing;
using LoogaSoft.Terrain.Instances;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class GroundExtraMapTests
    {
        [Test]
        public void CustomGroundInputsPreserveChannelsAndSharedTileEdges()
        {
            Assert.Zero(Shader.GetGlobalInt("_LoogaGroundCount"));
            Scene scene = EditorSceneManager.NewPreviewScene();
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 8, 32) };
            var firstMap = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            var secondMap = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            var material = new Material(Shader.Find("Hidden/Looga/Ground Extra Probe"));
            var sample = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGBFloat);
            try
            {
                firstMap.SetPixel(0, 0, new Color(0.2f, 0.3f, 0.4f, 0.5f));
                secondMap.SetPixel(0, 0, new Color(0.6f, 0.7f, 0.8f, 0.9f));
                firstMap.Apply();
                secondMap.Apply();
                var surfaces = new TerrainGroundBindings.Surface[2];
                for (int i = 0; i < 2; i++)
                {
                    var root = new GameObject("Extra map fixture " + i);
                    SceneManager.MoveGameObjectToScene(root, scene);
                    root.transform.position = Vector3.right * (i * 32);
                    var terrain = root.AddComponent<UnityEngine.Terrain>();
                    terrain.terrainData = data;
                    terrain.allowAutoConnect = false;
                    terrain.drawHeightmap = false;
                    surfaces[i] = new TerrainGroundBindings.Surface
                    {
                        Terrain = terrain,
                        Tint = Color.white,
                        ExtraMap = i == 0 ? firstMap : secondMap,
                        ExtraData = Vector4.one * (i == 0 ? 2 : 4)
                    };
                }
                var owner = new GameObject("Ground data bindings");
                SceneManager.MoveGameObjectToScene(owner, scene);
                var bindings = owner.AddComponent<TerrainGroundBindings>();
                bindings.Configure(surfaces);
                bindings.Publish();
                sample.Create();
                Match(new Color(0.2f, 0.3f, 0.4f, 0.5f), Read(material, sample, 16, 0));
                Match(new Color(0.4f, 0.5f, 0.6f, 0.7f), Read(material, sample, 32, 0));
                Match(new Color(3, 3, 3, 3), Read(material, sample, 32, 1));
                Match(Color.magenta, Read(material, sample, -1, 0));
                bindings.Configure(System.Array.Empty<TerrainGroundBindings.Surface>());
                bindings.Publish();
                Assert.AreSame(Texture2D.whiteTexture, Shader.GetGlobalTexture("_LoogaGroundExtra0"));
                Match(Color.magenta, Read(material, sample, 32, 0));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(firstMap);
                Object.DestroyImmediate(secondMap);
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(sample);
            }
        }

        private static Color Read(Material material, RenderTexture sample, float x, int channel)
        {
            material.SetVector("_ProbeWorld", new Vector4(x, 16, 0, 0));
            material.SetInt("_ProbeChannel", channel);
            using var command = new CommandBuffer();
            command.SetRenderTarget(sample);
            command.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3);
            Graphics.ExecuteCommandBuffer(command);
            var request = AsyncGPUReadback.Request(sample, 0, TextureFormat.RGBAFloat);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError);
            return request.GetData<Color>()[5];
        }

        private static void Match(Color expected, Color actual)
        {
            Assert.Less(Vector4.Distance(expected, actual), 0.0001f);
        }
    }
}
