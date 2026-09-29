using LoogaSoft.Instancing;
using LoogaSoft.Terrain.Instances;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Editor
{
    [InitializeOnLoad, CustomEditor(typeof(TerrainVegetationProvider))]
    internal sealed class TerrainVegetationProviderEditor : UnityEditor.Editor
    {
        static TerrainVegetationProviderEditor()
        {
            Undo.undoRedoPerformed += InvalidateSources;
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var provider = (TerrainVegetationProvider)target;
            EditorGUILayout.HelpBox("Native terrain remains the authoring source. Looga takes rendering ownership in Play mode only after validation.", MessageType.Info);
            EditorGUILayout.LabelField("Render owner", provider.IsRendering ? "Looga" : "Native / external");
            EditorGUILayout.LabelField("Resident instances", provider.InstanceCount.ToString());
            EditorGUILayout.LabelField("GPU buffer bytes", provider.GpuBytes.ToString());
            if (!string.IsNullOrEmpty(provider.Diagnostic))
            {
                EditorGUILayout.HelpBox(provider.Diagnostic, MessageType.Warning);
            }
            if (GUILayout.Button("Refresh Source"))
            {
                provider.InvalidateAll();
                if (Application.IsPlaying(provider.gameObject))
                {
                    provider.ProcessChanges();
                }
            }
        }

        private static void InvalidateSources()
        {
            foreach (var provider in Object.FindObjectsByType<TerrainVegetationProvider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                provider.InvalidateAll();
            }
        }
    }
}
