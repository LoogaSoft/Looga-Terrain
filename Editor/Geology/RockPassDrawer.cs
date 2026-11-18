using LoogaSoft.Terrain.Geology;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>Shows only the settings of the kind of a rock pass.</summary>
    [CustomPropertyDrawer(typeof(RockPass))]
    internal sealed class RockPassDrawer : PropertyDrawer
    {
        private static readonly string[] CommonFields = { nameof(RockPass.Kind), nameof(RockPass.Skip), nameof(RockPass.Seed) };
        private static readonly string[] EdgeFields = { nameof(RockPass.EdgeFocus), nameof(RockPass.EdgeRadius) };

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            float height = EditorGUIUtility.singleLineHeight;
            if (!property.isExpanded) return height;
            foreach (string field in CommonFields) height += FieldHeight(property.FindPropertyRelative(field));
            if (HasEdgeFocus(property))
            {
                foreach (string field in EdgeFields) height += FieldHeight(property.FindPropertyRelative(field));
            }
            foreach (SerializedProperty child in Children(property)) height += FieldHeight(child);
            return height;
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var row = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            label.text = $"{label.text}: {ObjectNames.NicifyVariableName(Kind(property).ToString())}";
            property.isExpanded = EditorGUI.Foldout(row, property.isExpanded, label, true);
            float y = row.yMax + EditorGUIUtility.standardVerticalSpacing;
            if (property.isExpanded)
            {
                EditorGUI.indentLevel++;
                foreach (string field in CommonFields) DrawField(position, ref y, property.FindPropertyRelative(field));
                if (HasEdgeFocus(property))
                {
                    foreach (string field in EdgeFields) DrawField(position, ref y, property.FindPropertyRelative(field));
                }
                foreach (SerializedProperty child in Children(property)) DrawField(position, ref y, child);
                EditorGUI.indentLevel--;
            }
            EditorGUI.EndProperty();
        }

        private static void DrawField(Rect area, ref float y, SerializedProperty field)
        {
            float height = EditorGUI.GetPropertyHeight(field, true);
            EditorGUI.PropertyField(new Rect(area.x, y, area.width, height), field, true);
            y += height + EditorGUIUtility.standardVerticalSpacing;
        }

        private static float FieldHeight(SerializedProperty field)
        {
            return EditorGUI.GetPropertyHeight(field, true) + EditorGUIUtility.standardVerticalSpacing;
        }

        private static RockPassKind Kind(SerializedProperty property)
        {
            return (RockPassKind)property.FindPropertyRelative(nameof(RockPass.Kind)).enumValueIndex;
        }

        private static bool HasEdgeFocus(SerializedProperty property)
        {
            return Kind(property) is RockPassKind.Noise or RockPassKind.Cells;
        }

        // The direct fields of the settings of the current kind.
        private static System.Collections.Generic.IEnumerable<SerializedProperty> Children(SerializedProperty property)
        {
            SerializedProperty settings = property.FindPropertyRelative(Kind(property).ToString());
            SerializedProperty child = settings.Copy();
            SerializedProperty end = settings.GetEndProperty();
            if (!child.NextVisible(true)) yield break;
            while (!SerializedProperty.EqualContents(child, end))
            {
                yield return child.Copy();
                if (!child.NextVisible(false)) yield break;
            }
        }
    }
}
