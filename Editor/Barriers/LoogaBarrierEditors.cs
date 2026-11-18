using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Barriers.Editor
{
    [CustomEditor(typeof(LoogaBarrierRun))]
    [CanEditMultipleObjects]
    public sealed class LoogaBarrierRunEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            DrawPropertiesExcluding(serializedObject, "m_Script");
            bool changed = EditorGUI.EndChangeCheck();
            serializedObject.ApplyModifiedProperties();
            // The inspector asks for the build. OnValidate does not, so loading a scene does not rebuild the runs.
            if (changed)
            {
                RequestRebuild();
            }

            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Rebuild"))
            {
                foreach (Object item in targets)
                {
                    ((LoogaBarrierRun)item).Rebuild();
                }
            }
            if (GUILayout.Button("Clear"))
            {
                foreach (Object item in targets)
                {
                    ((LoogaBarrierRun)item).Clear();
                }
            }
            EditorGUILayout.EndHorizontal();

            var run = (LoogaBarrierRun)target;
            if (!string.IsNullOrEmpty(run.Diagnostic))
            {
                EditorGUILayout.HelpBox(run.Diagnostic, MessageType.Warning);
            }
            else if (run.PieceCount > 0)
            {
                EditorGUILayout.HelpBox(run.PieceCount + " pieces, " + run.PostCount + " posts, " + run.GateCount + " gates.",
                    MessageType.None);
            }
            EditorGUILayout.HelpBox("Each spline in the container is one section. Add a spline to start a separate section; " +
                                    "the space between sections is an opening. Drag a gate handle in the Scene view " +
                                    "to move the gate along its spline.", MessageType.Info);
        }

        // Each gate has a handle. The handle moves the gate to the nearest point of its spline.
        private void OnSceneGUI()
        {
            var run = (LoogaBarrierRun)target;
            var container = run.GetComponent<SplineContainer>();
            if (!container || run.Gates.Count == 0)
                return;

            var runObject = new SerializedObject(run);
            SerializedProperty gates = runObject.FindProperty("_gates");
            for (int i = 0; i < gates.arraySize; i++)
            {
                SerializedProperty gate = gates.GetArrayElementAtIndex(i);
                int splineIndex = gate.FindPropertyRelative("_spline").intValue;
                if (splineIndex < 0 || splineIndex >= container.Splines.Count)
                {
                    continue;
                }

                SerializedProperty position = gate.FindPropertyRelative("_position");
                Vector3 point = container.EvaluatePosition(splineIndex, position.floatValue);
                float size = HandleUtility.GetHandleSize(point) * 0.12f;
                var kind = (BarrierGateKind)gate.FindPropertyRelative("_kind").enumValueIndex;
                Handles.color = kind == BarrierGateKind.Closed ? new Color(1f, 0.6f, 0.1f) : kind == BarrierGateKind.Open ? Color.green : Color.cyan;
                Handles.Label(point + Vector3.up * size * 3f, "Gate " + i);
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.FreeMoveHandle(point, size, Vector3.zero, Handles.SphereHandleCap);
                if (!EditorGUI.EndChangeCheck())
                {
                    continue;
                }

                float3 local = container.transform.InverseTransformPoint(moved);
                SplineUtility.GetNearestPoint(container.Splines[splineIndex], local, out float3 _, out float t);
                position.floatValue = t;
                runObject.ApplyModifiedProperties();
                run.RequestRebuild();
            }
        }

        private void RequestRebuild()
        {
            foreach (Object item in targets)
            {
                ((LoogaBarrierRun)item).RequestRebuild();
            }
        }
    }

    [CustomEditor(typeof(LoogaBarrierSet))]
    public sealed class LoogaBarrierSetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUI.BeginChangeCheck();
            DrawPropertiesExcluding(serializedObject, "m_Script");
            bool changed = EditorGUI.EndChangeCheck();
            serializedObject.ApplyModifiedProperties();
            if (!changed)
                return;

            // Every run that uses this set builds again with the new settings.
            foreach (LoogaBarrierRun run in Object.FindObjectsByType<LoogaBarrierRun>(FindObjectsSortMode.None))
            {
                if (run.Set == target)
                {
                    run.RequestRebuild();
                }
            }
        }
    }
}
