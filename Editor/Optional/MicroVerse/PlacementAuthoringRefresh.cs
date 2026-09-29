using LoogaSoft.Terrain.Integrations;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Editor
{
    [InitializeOnLoad]
    internal sealed class PlacementAuthoringRefresh : AssetPostprocessor
    {
        private static bool _queued;
        static PlacementAuthoringRefresh()
        {
            Undo.undoRedoPerformed += Queue;
            EditorApplication.hierarchyChanged += Queue;
        }
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] previous)
        {
            if (imported.Length + deleted.Length + moved.Length > 0)
            {
                Queue();
            }
        }
        private static void Queue()
        {
            if (_queued || EditorApplication.isPlayingOrWillChangePlaymode) return;
            _queued = true;
            EditorApplication.delayCall += Refresh;
        }
        private static void Refresh()
        {
            _queued = false;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            foreach (var bridge in Object.FindObjectsByType<MicroVersePlacementBridge>(FindObjectsSortMode.None))
            {
                if (bridge && bridge.isActiveAndEnabled && bridge.gameObject.scene.isLoaded)
                {
                    bridge.RequestRefresh();
                }
            }
        }
    }
}
