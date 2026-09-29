using LoogaSoft.Terrain.Geology;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Editor
{
    [CustomEditor(typeof(BasaltArch))]
    public sealed class BasaltArchEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "Shape the entire arch with the Unity Spline tool. The component extrudes one continuous, closed basalt column along that spline; moving or inserting knots rebuilds the mesh automatically.",
                MessageType.Info);

            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script");
            bool changed = serializedObject.ApplyModifiedProperties();

            BasaltArch arch = (BasaltArch)target;
            if (changed) arch.RebuildNow();
            if (!string.IsNullOrEmpty(arch.Diagnostic))
                EditorGUILayout.HelpBox(arch.Diagnostic, MessageType.Warning);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Rebuild From Spline"))
                {
                    arch.RebuildNow();
                    SceneView.RepaintAll();
                }
                if (GUILayout.Button("Clear Generated Mesh"))
                {
                    arch.ClearGeneratedMesh();
                    SceneView.RepaintAll();
                }
            }
        }

        [MenuItem("GameObject/Looga/Create Basalt Arch", false, 12)]
        private static void Create(MenuCommand menuCommand)
        {
            GameObject gameObject = new("Basalt Arch");
            Undo.RegisterCreatedObjectUndo(gameObject, "Create basalt arch");
            GameObjectUtility.SetParentAndAlign(gameObject, menuCommand.context as GameObject);

            SplineContainer container = gameObject.AddComponent<SplineContainer>();
            Spline spline = new();
            spline.Add(new BezierKnot(new float3(-50f, 0f, 0f)), TangentMode.AutoSmooth);
            spline.Add(new BezierKnot(new float3(0f, 45f, 0f)), TangentMode.AutoSmooth);
            spline.Add(new BezierKnot(new float3(50f, 0f, 0f)), TangentMode.AutoSmooth);
            container.Spline = spline;

            BasaltArch arch = gameObject.AddComponent<BasaltArch>();
            arch.Configure(container, null, 8f, 7f,
                UnityEngine.Random.Range(1, int.MaxValue));
            arch.RebuildNow();
            Selection.activeGameObject = gameObject;
        }
    }
}
