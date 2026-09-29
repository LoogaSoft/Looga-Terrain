using UnityEditor;

namespace LoogaSoft.Terrain.Editor
{
    [CustomEditor(typeof(TerrainWaterCamera))]
    internal sealed class TerrainWaterCameraEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var camera = (TerrainWaterCamera)target;
            EditorGUILayout.HelpBox("Requests URP depth and opaque textures for water. Disable to restore previous camera options.", MessageType.Info);
            if (!string.IsNullOrEmpty(camera.Diagnostic))
            {
                EditorGUILayout.HelpBox(camera.Diagnostic, MessageType.Warning);
            }
        }
    }
}
