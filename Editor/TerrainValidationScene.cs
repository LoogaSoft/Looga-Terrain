using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>
    /// Creates an isolated sample scene without copying or editing project terrain data.
    /// </summary>
    public static class TerrainValidationScene
    {
        public const string PackagePath = "Packages/com.loogasoft.loogaterrain";
        public const string ScenePath = "Assets/Tests/LoogaTerrain/LoogaTerrain_Validation.unity";

        /// <summary>
        /// Creates two synthetic tiles with different heightmap resolutions and shared border heights.
        /// </summary>
        [MenuItem("Tools/Looga/Terrain/Create Validation Scene")]
        public static void Create()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath))
            {
                if (!SceneManager.GetSceneByPath(ScenePath).isLoaded)
                {
                    Scene previous = SceneManager.GetActiveScene();
                    EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
                    SceneManager.SetActiveScene(previous);
                }
                return;
            }

            EnsureFolder("Assets", "Tests");
            EnsureFolder("Assets/Tests", "LoogaTerrain");
            Scene originalScene = SceneManager.GetActiveScene();
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);
            try
            {
                ComputeShader selection = AssetDatabase.LoadAssetAtPath<ComputeShader>(PackagePath + "/Runtime/Shaders/TerrainSelection.compute");
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(PackagePath + "/Runtime/Shaders/TerrainLit.shader");
                if (!selection || !shader || ShaderUtil.ShaderHasError(shader))
                {
                    throw new InvalidOperationException("Looga Terrain shaders must compile before scene creation.");
                }
                var material = new Material(shader) { name = "Looga Terrain Validation" };
                AssetDatabase.CreateAsset(material, "Assets/Tests/LoogaTerrain/Validation.mat");
                for (int tile = 0; tile < 2; tile++)
                {
                    CreateTile(tile, selection, material);
                }

                var cameraObject = new GameObject("Looga Terrain Validation Camera", typeof(Camera));
                cameraObject.layer = 31;
                Camera camera = cameraObject.GetComponent<Camera>();
                camera.cullingMask = 1 << 31;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 2000f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.12f, 0.2f, 0.3f);
                camera.transform.position = new Vector3(10128f, 130f, 9850f);
                camera.transform.LookAt(new Vector3(10128f, 10f, 10064f));
                camera.enabled = false;
                var sunObject = new GameObject("Looga Terrain Validation Sun", typeof(Light));
                Light sun = sunObject.GetComponent<Light>();
                sun.type = LightType.Directional;
                sun.cullingMask = 1 << 31;
                sun.intensity = 1.3f;
                sun.shadows = LightShadows.Soft;
                sun.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
                GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
                marker.name = "Shadow and Scale Reference";
                marker.layer = 31;
                marker.transform.position = new Vector3(10110f, 22f, 10065f);
                marker.transform.localScale = new Vector3(5f, 18f, 5f);
                EditorSceneManager.SaveScene(scene, ScenePath);
                AssetDatabase.SaveAssets();
            }
            finally
            {
                SceneManager.SetActiveScene(originalScene);
            }
        }

        private static void CreateTile(int tile, ComputeShader selection, Material material)
        {
            int resolution = tile == 0 ? 129 : 257;
            var data = new TerrainData { name = "Validation Tile " + tile, heightmapResolution = resolution, size = new Vector3(128f, 40f, 128f) };
            var heights = new float[resolution, resolution];
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    float worldX = tile * 128f + x * 128f / (resolution - 1);
                    float worldZ = z * 128f / (resolution - 1);
                    heights[z, x] = Height(worldX, worldZ);
                    if (tile == 1 && x == 0 && z % 2 == 1)
                    {
                        heights[z, x] = (Height(worldX, worldZ - 0.5f) + Height(worldX, worldZ + 0.5f)) * 0.5f;
                    }
                }
            }
            data.SetHeights(0, 0, heights);
            var holes = new bool[resolution - 1, resolution - 1];
            for (int z = 0; z < resolution - 1; z++)
            {
                for (int x = 0; x < resolution - 1; x++)
                {
                    holes[z, x] = tile != 1 || x < 155 || x > 171 || z < 155 || z > 171;
                }
            }
            data.SetHoles(0, 0, holes);
            AssetDatabase.CreateAsset(data, "Assets/Tests/LoogaTerrain/Tile" + tile + ".asset");
            GameObject terrainObject = UnityEngine.Terrain.CreateTerrainGameObject(data);
            terrainObject.name = "Looga Validation Tile " + tile;
            terrainObject.layer = 31;
            terrainObject.transform.position = new Vector3(10000f + tile * 128f, 0f, 10000f);
            var terrain = terrainObject.GetComponent<UnityEngine.Terrain>();
            terrain.allowAutoConnect = false;
            terrain.drawInstanced = true;
            terrainObject.AddComponent<TerrainRenderer>().Configure(selection, material, 32);
        }

        private static float Height(float x, float z)
        {
            float height = 0.25f + 0.14f * Mathf.Sin(x * 0.024f) * Mathf.Cos(z * 0.025f) + 0.08f * Mathf.Sin(z * 0.065f);
            // Even encoded heights let the finer border represent each midpoint exactly.
            return Mathf.Round(height * 16383f) / 16383f;
        }

        private static void EnsureFolder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder(parent + "/" + name))
            {
                AssetDatabase.CreateFolder(parent, name);
            }
        }
    }
}
