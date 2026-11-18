using System.Collections.Generic;
using LoogaSoft.Inspector.Editor;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps.Editor
{
    /// <summary>Shows the limits that read a height snapshot.</summary>
    [CustomEditor(typeof(LoogaHeightSnapshot))]
    internal sealed class LoogaHeightSnapshotEditor : UnityEditor.Editor
    {
        private readonly List<LoogaSnapshotLimit> _limits = new();

        public override void OnInspectorGUI()
        {
            LoogaBanner.DrawFor(target.GetType());
            DrawDefaultInspector();
            ((LoogaHeightSnapshot)target).GetLimits(_limits);
            if (_limits.Count == 0)
            {
                EditorGUILayout.HelpBox("No snapshot limit reads this snapshot. The evaluator skips it.",
                    MessageType.Info);
                return;
            }
            EditorGUILayout.LabelField("Read By", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                foreach (LoogaSnapshotLimit limit in _limits)
                {
                    EditorGUILayout.ObjectField(limit, typeof(LoogaSnapshotLimit), true);
                }
            }
        }
    }

    /// <summary>Warns when a snapshot limit cannot read its snapshot.</summary>
    [CustomEditor(typeof(LoogaSnapshotLimit))]
    internal sealed class LoogaSnapshotLimitEditor : UnityEditor.Editor
    {
        private readonly List<LoogaHeightModifier> _modifiers = new();

        public override void OnInspectorGUI()
        {
            LoogaBanner.DrawFor(target.GetType());
            DrawDefaultInspector();
            string problem = FindProblem((LoogaSnapshotLimit)target);
            if (problem != null)
            {
                EditorGUILayout.HelpBox(problem, MessageType.Warning);
            }
        }

        // Null when the limit reads its snapshot.
        private string FindProblem(LoogaSnapshotLimit limit)
        {
            if (!limit.Snapshot) return "No snapshot is set. The limit has no effect.";
            var world = limit.GetComponentInParent<LoogaStampWorld>();
            if (!world) return "The limit is not under a stamp world.";
            if (!limit.isActiveAndEnabled) return null;
            world.GetHeightModifiers(_modifiers);
            int snapshot = _modifiers.IndexOf(limit.Snapshot);
            if (snapshot < 0) return "The snapshot is disabled or not in this stamp world. The limit has no effect.";
            if (snapshot > _modifiers.IndexOf(limit))
            {
                return "The snapshot comes after the limit in the stamp order. The limit has no effect.";
            }
            return null;
        }
    }
}
