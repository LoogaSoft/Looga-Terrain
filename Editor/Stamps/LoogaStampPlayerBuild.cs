using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LoogaSoft.Terrain.Stamps.Editor
{
    /// <summary>
    /// Leaves the stamp textures out of player builds: the height maps, falloff textures and masks of the height, texture
    /// and placement modifiers.
    /// </summary>
    /// <remarks>
    /// A player does not update the stamp world, because the terrain data holds the heights, the textures and the
    /// placements. Only a rock generator on a <see cref="LoogaSnapshotGround"/> evaluates stamps, and the player build
    /// ships its meshes or stops (<see cref="LoogaSnapshotGround.ReadableInPlayer"/>). Without this step, the textures
    /// use GPU memory in the player (2.2 GB in Kubera World). The result depends only on the scene, so an incremental
    /// build can keep a scene that it processed before. Increase the callback version when the step changes.
    /// </remarks>
    [BuildCallbackVersion(1)]
    internal sealed class LoogaStampPlayerBuild : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            // Play mode in the editor processes scenes without a report.
            if (report == null) return;

            int modifiers = 0;
            int textures = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (!IsModifier(behaviour)) continue;

                    int cleared = ClearTextures(behaviour);
                    if (cleared > 0)
                    {
                        modifiers++;
                        textures += cleared;
                    }
                }
            }
            if (textures == 0) return;

            Debug.Log("[Looga Terrain] Stamps of scene " + scene.name + ": " + textures + " texture references of " +
                modifiers + " modifiers left out of the build. A player does not update the stamp world.");
        }

        private static bool IsModifier(MonoBehaviour behaviour)
        {
            return behaviour is LoogaHeightModifier || behaviour is LoogaTextureModifier ||
                behaviour is LoogaPlacementModifier;
        }

        // Clears each texture reference of the serialized fields, also in nested settings such as the falloff.
        private static int ClearTextures(MonoBehaviour behaviour)
        {
            var serialized = new SerializedObject(behaviour);
            SerializedProperty property = serialized.GetIterator();
            int cleared = 0;
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (property.objectReferenceValue is not Texture) continue;

                property.objectReferenceValue = null;
                cleared++;
            }
            if (cleared > 0)
            {
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            return cleared;
        }
    }
}
