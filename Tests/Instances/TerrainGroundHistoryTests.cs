using LoogaSoft.Instancing;
using System.Collections;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace LoogaSoft.Terrain.Instances.Tests
{
    public sealed class TerrainGroundHistoryTests
    {
        [UnityTest]
        public IEnumerator HeightHistoryAdvancesOncePerFrameAndClearsRemovedSources()
        {
            Assert.Zero(Shader.GetGlobalInt("_LoogaGroundCount"), "This fixture needs no existing ground owner.");
            Scene scene = EditorSceneManager.NewPreviewScene();
            var data = new TerrainData { heightmapResolution = 33, size = new Vector3(32, 8, 32) };
            var replacement = new TerrainData { heightmapResolution = 65, size = new Vector3(32, 8, 32) };
            try
            {
                var root = new GameObject("History fixture");
                SceneManager.MoveGameObjectToScene(root, scene);
                var terrain = root.AddComponent<UnityEngine.Terrain>();
                terrain.terrainData = data;
                terrain.allowAutoConnect = false;
                terrain.drawHeightmap = false;
                var binding = root.AddComponent<TerrainGroundBindings>();
                binding.Configure(new[] { new TerrainGroundBindings.Surface { Terrain = terrain, Tint = Color.white } });
                binding.Publish();
                Texture initial = Shader.GetGlobalTexture("_LoogaGroundPreviousHeight0");
                Assert.AreNotSame(data.heightmapTexture, initial);
                float[,] heights = new float[33, 33];
                for (int y = 0; y < 33; y++)
                {
                    for (int x = 0; x < 33; x++)
                    {
                        heights[y, x] = 0.4f;
                    }
                }
                data.SetHeights(0, 0, heights);
                yield return NextEditorUpdate();
                binding.Publish();
                Assert.AreSame(initial, Shader.GetGlobalTexture("_LoogaGroundPreviousHeight0"));
                Assert.Less(ReadCenter(initial), 0.001f);
                Assert.Greater(ReadCenter(data.heightmapTexture), 0.1f);
                binding.Publish();
                Assert.AreSame(initial, Shader.GetGlobalTexture("_LoogaGroundPreviousHeight0"));
                yield return NextEditorUpdate();
                binding.Publish();
                Texture advanced = Shader.GetGlobalTexture("_LoogaGroundPreviousHeight0");
                Assert.AreNotSame(initial, advanced);
                Assert.That(ReadCenter(advanced), Is.EqualTo(ReadCenter(data.heightmapTexture)).Within(0.0001f));
                terrain.terrainData = replacement;
                binding.Publish();
                Assert.AreEqual(65, Shader.GetGlobalTexture("_LoogaGroundPreviousHeight0").width);
                binding.Configure(System.Array.Empty<TerrainGroundBindings.Surface>());
                binding.Publish();
                Assert.Zero(Shader.GetGlobalInt("_LoogaGroundCount"));
                Assert.AreSame(Texture2D.blackTexture, Shader.GetGlobalTexture("_LoogaGroundPreviousHeight0"));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                Object.DestroyImmediate(data);
                Object.DestroyImmediate(replacement);
            }
        }

        private static IEnumerator NextEditorUpdate()
        {
            bool advanced = false;
            void Updated() => advanced = true;
            UnityEditor.EditorApplication.update += Updated;
            try
            {
                while (!advanced)
                {
                    yield return null;
                }
            }
            finally
            {
                UnityEditor.EditorApplication.update -= Updated;
            }
        }

        private static float ReadCenter(Texture texture)
        {
            var request = AsyncGPUReadback.Request(texture, 0, TextureFormat.RFloat);
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError);
            var values = request.GetData<float>();
            return values[(texture.height / 2) * texture.width + texture.width / 2];
        }
    }
}
