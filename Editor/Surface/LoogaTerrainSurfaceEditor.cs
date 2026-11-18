using UnityEditor;
using UnityEngine;
using ArrayState = LoogaSoft.Terrain.Surface.Editor.LoogaTerrainSurfaceArrayBuilder.ArrayState;
using Status = LoogaSoft.Terrain.Surface.Editor.LoogaTerrainSurfaceArrayBuilder.Status;

namespace LoogaSoft.Terrain.Surface.Editor
{
    /// <summary>
    /// Inspector of a Looga terrain surface. The array builder owns the texture arrays, so the inspector shows them
    /// read-only with their build state, a progress bar while a build runs, and a Rebuild button.
    /// </summary>
    [CustomEditor(typeof(LoogaTerrainSurface))]
    internal sealed class LoogaTerrainSurfaceEditor : UnityEditor.Editor
    {
        private static readonly int[] Resolutions = { 256, 512, 1024, 2048, 4096 };
        private static readonly GUIContent[] ResolutionLabels =
        {
            new("256"), new("512"), new("1024"), new("2048"), new("4096")
        };

        public override bool RequiresConstantRepaint()
        {
            return LoogaTerrainSurfaceAutoBuild.IsBuilding((LoogaTerrainSurface)target, out _, out _);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            SerializedProperty property = serializedObject.GetIterator();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                switch (property.name)
                {
                    case "m_Script":
                    case "_albedoArray":
                    case "_normalArray":
                    case "_maskArray":
                        using (new EditorGUI.DisabledScope(true))
                        {
                            EditorGUILayout.PropertyField(property, true);
                        }
                        // The state describes one surface. Multi-object editing shows the fields only.
                        if (property.name == "_maskArray" && targets.Length == 1)
                        {
                            DrawArrayState((LoogaTerrainSurface)target);
                        }
                        break;
                    case "_arrayResolution":
                        EditorGUILayout.IntPopup(property, ResolutionLabels, Resolutions);
                        break;
                    default:
                        EditorGUILayout.PropertyField(property, true);
                        break;
                }
            }
            serializedObject.ApplyModifiedProperties();
        }

        private static void DrawArrayState(LoogaTerrainSurface surface)
        {
            if (LoogaTerrainSurfaceAutoBuild.IsBuilding(surface, out float progress, out string description))
            {
                Rect bar = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                EditorGUI.ProgressBar(bar, progress, description);
                if (GUILayout.Button("Cancel"))
                {
                    LoogaTerrainSurfaceAutoBuild.Cancel();
                }
                return;
            }
            if (!LoogaTerrainSurfaceAutoBuild.TryGetStatus(surface, out Status status, out string error))
            {
                EditorGUILayout.HelpBox("Cannot check the arrays. " + error, MessageType.Warning);
                return;
            }
            EditorGUILayout.HelpBox(Describe(surface, status), Type(surface, status));
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Rebuild", "Build the arrays of the selected layout now. Layers in " +
                        "the slice cache are copied, the other layers are packed from the source maps."), GUILayout.Width(100f)))
                {
                    LoogaTerrainSurfaceAutoBuild.Rebuild(surface);
                }
            }
        }

        private static string Describe(LoogaTerrainSurface surface, Status status)
        {
            string memory = (status.GpuBytes / (1024.0 * 1024.0)).ToString("F1") + " MB GPU memory";
            string work = status.LayersToPack == 0
                ? "All " + status.LayerCount + " layers are in the slice cache."
                : status.LayersToPack + " of " + status.LayerCount + " layers need packing from the source maps.";
            string next = surface.AutoRebuild ? "The build starts automatically." : "Auto Rebuild is off. Use Rebuild.";
            if (LoogaTerrainSurfaceAutoBuild.HasFailed(surface))
            {
                next = "The last build failed (see the console). Fix the problem, then use Rebuild.";
            }
            switch (status.State)
            {
                case ArrayState.UpToDate:
                    return status.Layout + " arrays are up to date. " + memory + ".";
                case ArrayState.Unassigned:
                    return status.Layout + " arrays are up to date but not assigned. They are assigned automatically.";
                case ArrayState.OutOfDate:
                    return status.Layout + " arrays are out of date. " + work + " " + next;
                default:
                    return status.Layout + " arrays do not exist (" + memory + " when built). " + work + " " + next;
            }
        }

        private static MessageType Type(LoogaTerrainSurface surface, Status status)
        {
            if (LoogaTerrainSurfaceAutoBuild.HasFailed(surface)) return MessageType.Error;
            return status.State == ArrayState.UpToDate ? MessageType.Info : MessageType.Warning;
        }
    }
}
