using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Editor
{
    [CustomEditor(typeof(TerrainRenderer))]
    public sealed class TerrainRendererEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var renderer = (TerrainRenderer)target;
            if (!string.IsNullOrEmpty(renderer.Diagnostic))
            {
                EditorGUILayout.HelpBox(renderer.Diagnostic, MessageType.Warning);
            }
            if (GUILayout.Button("Apply Renderer Settings"))
            {
                renderer.Configure(
                    (ComputeShader)serializedObject.FindProperty("_selectionCompute").objectReferenceValue,
                    (Material)serializedObject.FindProperty("_surfaceMaterial").objectReferenceValue,
                    serializedObject.FindProperty("_patchCount").intValue);
                EditorUtility.SetDirty(renderer);
            }
        }
    }
}
