using LoogaSoft.Terrain.Instances;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Editor
{
    [CustomEditor(typeof(VegetationSettings))]
    public sealed class VegetationSettingsEditor : UnityEditor.Editor
    {
        private bool _details = true;
        private bool _trees;
        private string _error;

        private void OnEnable()
        {
            Undo.undoRedoPerformed += OnUndo;
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= OnUndo;
        }

        private void OnUndo()
        {
            if (!(target is VegetationSettings profile) || !profile) return;
            profile.NotifyChanged();
            _error = null;
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            var profile = (VegetationSettings)target;
            bool gpuDetails = EditorGUILayout.Toggle(new GUIContent("GPU-Resident Details",
                "Generate mesh detail placements on the GPU. Placement differs from native terrain details."),
                profile.GpuResidentDetails);
            if (gpuDetails != profile.GpuResidentDetails)
            {
                Undo.RecordObject(profile, "Change GPU-Resident Details");
                profile.SetGpuResidentDetails(gpuDetails);
                EditorUtility.SetDirty(profile);
            }
            _details = EditorGUILayout.Foldout(_details, "Detail defaults", true);
            if (_details) LoogaWorldControllerEditor.DrawDefaults(profile, VegetationKind.Detail, ref _error);
            _trees = EditorGUILayout.Foldout(_trees, "Tree defaults", true);
            if (_trees) LoogaWorldControllerEditor.DrawDefaults(profile, VegetationKind.Tree, ref _error);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Source overrides", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Select Looga World in the scene to inspect the source catalog and edit individual override fields.", MessageType.Info);
            foreach (var entry in profile.Overrides)
            {
                if (entry == null) continue;
                EditorGUILayout.ObjectField(entry.Kind.ToString(), entry.Source, typeof(Object), false);
                EditorGUILayout.LabelField(entry.Fields.ToString(), EditorStyles.wordWrappedMiniLabel);
            }
            if (!string.IsNullOrEmpty(_error)) EditorGUILayout.HelpBox(_error, MessageType.Error);
        }
    }
}
