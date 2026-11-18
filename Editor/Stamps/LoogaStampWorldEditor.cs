using System.Collections.Generic;
using System.Diagnostics;
using LoogaSoft.Inspector.Editor;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps.Editor
{
    [CustomEditor(typeof(LoogaStampWorld))]
    internal sealed class LoogaStampWorldEditor : UnityEditor.Editor
    {
        private readonly List<LoogaHeightModifier> _modifiers = new();
        private bool _showOrder;
        private string _lastRebuild;

        public override void OnInspectorGUI()
        {
            LoogaBanner.DrawFor(target.GetType());
            DrawDefaultInspector();
            var world = (LoogaStampWorld)target;

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(world.Terrains.Count == 0))
            {
                if (GUILayout.Button("Rebuild Heights"))
                {
                    Rebuild(world);
                }
            }
            if (!string.IsNullOrEmpty(_lastRebuild))
            {
                EditorGUILayout.HelpBox(_lastRebuild, MessageType.Info);
            }

            world.GetHeightModifiers(_modifiers);
            _showOrder = EditorGUILayout.Foldout(_showOrder, "Apply Order (" + _modifiers.Count + " height modifiers)",
                true);
            if (!_showOrder) return;
            using (new EditorGUI.DisabledScope(true))
            {
                for (int i = 0; i < _modifiers.Count; i++)
                {
                    LoogaHeightModifier modifier = _modifiers[i];
                    string label = (i + 1) + ". " + (modifier is LoogaHeightStamp stamp ? stamp.Mode.ToString() :
                        "Path");
                    EditorGUILayout.ObjectField(label, modifier, typeof(LoogaHeightModifier), true);
                }
            }
        }

        // No undo record: a full copy of every heightmap costs too much memory. The stamps are the source data.
        private void Rebuild(LoogaStampWorld world)
        {
            Stopwatch timer = Stopwatch.StartNew();
            world.RebuildHeights();
            _lastRebuild = "Rebuilt " + world.Terrains.Count + " terrains in " + timer.ElapsedMilliseconds + " ms.";
        }
    }
}
