using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class TerrainWaterCameraTests
    {
        [Test]
        public void SerializedCameraLeaseRestoresOriginalOptions()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var original = new GameObject("Original camera");
                SceneManager.MoveGameObjectToScene(original, scene);
                original.AddComponent<Camera>().enabled = false;
                var originalData = original.AddComponent<UniversalAdditionalCameraData>();
                originalData.requiresDepthOption = CameraOverrideOption.Off;
                originalData.requiresColorOption = CameraOverrideOption.UsePipelineSettings;
                var originalBridge = original.AddComponent<TerrainWaterCamera>();
                string state = JsonUtility.ToJson(originalBridge);

                var restored = new GameObject("Restored camera");
                SceneManager.MoveGameObjectToScene(restored, scene);
                restored.SetActive(false);
                restored.AddComponent<Camera>().enabled = false;
                var restoredData = restored.AddComponent<UniversalAdditionalCameraData>();
                restoredData.requiresDepthOption = CameraOverrideOption.On;
                restoredData.requiresColorOption = CameraOverrideOption.On;
                var restoredBridge = restored.AddComponent<TerrainWaterCamera>();
                JsonUtility.FromJsonOverwrite(state, restoredBridge);
                restored.SetActive(true);
                restoredBridge.enabled = false;
                Assert.That(restoredData.requiresDepthOption, Is.EqualTo(CameraOverrideOption.Off));
                Assert.That(restoredData.requiresColorOption, Is.EqualTo(CameraOverrideOption.UsePipelineSettings));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [TestCase(CameraOverrideOption.Off)]
        [TestCase(CameraOverrideOption.UsePipelineSettings)]
        [TestCase(CameraOverrideOption.On)]
        public void DisableRestoresCameraTextureOptions(CameraOverrideOption previous)
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("Water camera test");
                SceneManager.MoveGameObjectToScene(root, scene);
                root.AddComponent<Camera>().enabled = false;
                var data = root.AddComponent<UniversalAdditionalCameraData>();
                data.requiresDepthOption = previous;
                data.requiresColorOption = previous;
                var bridge = root.AddComponent<TerrainWaterCamera>();
                Assert.That(data.requiresDepthTexture, Is.True);
                Assert.That(data.requiresColorTexture, Is.True);
                bridge.enabled = false;
                Assert.That(data.requiresDepthOption, Is.EqualTo(previous));
                Assert.That(data.requiresColorOption, Is.EqualTo(previous));
                bridge.enabled = true;
                data.requiresColorOption = CameraOverrideOption.Off;
                bridge.enabled = false;
                Assert.That(data.requiresColorOption, Is.EqualTo(CameraOverrideOption.Off));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }
    }
}
