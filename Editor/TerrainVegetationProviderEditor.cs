using LoogaSoft.Inspector.Editor;
using LoogaSoft.Instancing;
using LoogaSoft.Terrain.Instances;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Editor
{
    // An undo does not refresh all providers: each provider refreshes in OnValidate when an undo restores its fields, and
    // finds terrain data, position, density and prototype changes itself. A refresh of all providers on every undo
    // rebuilt every vegetation stream and redrew the cached Looga shadows for about 10 s.
    [CustomEditor(typeof(TerrainVegetationProvider))]
    internal sealed class TerrainVegetationProviderEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            LoogaBanner.DrawFor(target.GetType());
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
    }
}
