using LoogaSoft.Inspector.Editor;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Editor
{
    [CustomEditor(typeof(TerrainRenderer))]
    public sealed class TerrainRendererEditor : UnityEditor.Editor
    {
        // Local values that an assigned TerrainRendererSettings asset replaces.
        private static readonly string[] SharedFields =
        {
            "m_Script", "_patchCount", "_errorPixels", "_frustumCulling", "_castShadows", "_batchSurfaceDraws",
            "_minimumSurfaceBatchTiles", "_shadowErrorTexels"
        };
        private UnityEditor.Editor _settingsEditor;

        private void OnDisable()
        {
            DestroyImmediate(_settingsEditor);
        }

        public override void OnInspectorGUI()
        {
            LoogaBanner.DrawFor(target.GetType());
            serializedObject.Update();
            var settings = (TerrainRendererSettings)serializedObject.FindProperty("_settings").objectReferenceValue;
            if (settings)
            {
                DrawPropertiesExcluding(serializedObject, SharedFields);
                serializedObject.ApplyModifiedProperties();
                EditorGUILayout.HelpBox("The shared settings below replace the local values. Edits change every " +
                    "terrain that uses " + settings.name + ".", MessageType.None);
                CreateCachedEditor(settings, null, ref _settingsEditor);
                _settingsEditor.OnInspectorGUI();
            }
            else
            {
                DrawDefaultInspector();
            }
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
