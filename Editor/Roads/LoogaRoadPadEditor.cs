using System;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Roads.Editor
{
    /// <summary>
    /// Shows the road links of a road pad and the pad tools: auto level, rounded corners and road linking.
    /// </summary>
    [CustomEditor(typeof(LoogaRoadPad))]
    public sealed class LoogaRoadPadEditor : UnityEditor.Editor
    {
        /// <summary>
        /// Draws more pad tools below the package tools. A project adds its own pad tools here, for example a
        /// blockout rebuild.
        /// </summary>
        public static event Action<LoogaRoadPad> ExtraTools;

        public override void OnInspectorGUI()
        {
            var pad = (LoogaRoadPad)target;
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_easeKnots"), new GUIContent("Ease Knots"));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Linked Road Knots", EditorStyles.boldLabel);
            if (pad.RoadLinks.Count == 0)
            {
                EditorGUILayout.HelpBox("No road knots are linked. Link Roads finds the knots near the pad.",
                    MessageType.Info);
            }
            foreach (LoogaRoadPad.RoadLink link in pad.RoadLinks)
            {
                EditorGUILayout.LabelField(link.IsFound ? link.ToString() : link + " (not found, use Link Roads)");
            }

            EditorGUILayout.Space();
            if (GUILayout.Button(new GUIContent("Auto Level",
                    "Sets the pad height to the median ground under the outline. Linked road knots move with the pad.")))
            {
                Debug.Log(LoogaRoadPads.AutoLevel(pad));
            }
            if (GUILayout.Button(new GUIContent("Round Corners",
                    "Makes each cut corner of the outline a quarter circle between the same two knots.")))
            {
                Debug.Log(LoogaRoadPads.RoundCorners(pad.transform));
            }
            LoogaRoadNetwork network = LoogaRoadPads.FindNetwork(pad.transform);
            using (new EditorGUI.DisabledScope(!network))
            {
                string margin = network ? network.PadLinkMargin.ToString("F0") : "the link margin";
                if (GUILayout.Button(new GUIContent("Link Roads",
                        "Links the road knots within " + margin + " m of the outline. It replaces the current links.")))
                {
                    Debug.Log(pad.name + ": " + LoogaRoadPads.LinkRoads(pad));
                }
            }
            ExtraTools?.Invoke(pad);
        }
    }
}
