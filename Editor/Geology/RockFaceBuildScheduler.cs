using LoogaSoft.Terrain.Geology;
using UnityEditor;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>
    /// Builds the waiting <see cref="LoogaRockFace"/> components in edit mode. Each editor update starts a few builds on
    /// worker threads and gives the meshes of finished builds, so a domain reload or a scene load does not stop the
    /// editor for all builds at once.
    /// </summary>
    [InitializeOnLoad]
    internal static class RockFaceBuildScheduler
    {
        static RockFaceBuildScheduler()
        {
            EditorApplication.update += OnEditorUpdate;
        }

        private static void OnEditorUpdate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (LoogaRockFace.PendingCount == 0) return;
            if (LoogaRockFace.UpdateBuilds()) SceneView.RepaintAll();
        }
    }
}
