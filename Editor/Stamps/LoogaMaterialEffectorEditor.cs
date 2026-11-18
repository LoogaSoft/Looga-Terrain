using System.Collections.Generic;
using LoogaSoft.Instancing;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Stamps.Editor
{
    /// <summary>
    /// Inspector of the Material Effector. The property of each value is chosen from the [LoogaEffectorTarget] properties
    /// of the stamp prototype materials. The value fields follow the property type.
    /// </summary>
    [CustomEditor(typeof(LoogaMaterialEffector))]
    internal sealed class LoogaMaterialEffectorEditor : UnityEditor.Editor
    {
        // One marked property of the prototypes and the number of prototypes whose materials mark it.
        private sealed class Available
        {
            public InstanceEffectorTargets.Target Target;
            public int Prototypes;
        }

        private readonly Dictionary<string, Available> _available = new();
        private readonly List<string> _names = new();
        private int _prototypeCount;
        private readonly Dictionary<int, bool> _expanded = new();

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            CollectAvailable(((LoogaMaterialEffector)target).GetComponent<LoogaObjectStamp>());
            if (_available.Count == 0)
            {
                EditorGUILayout.HelpBox("No prototype material of this stamp marks a property with [LoogaEffectorTarget]. " +
                    "Mark the properties in the shader to change them here.", MessageType.Info);
            }

            SerializedProperty variants = serializedObject.FindProperty("_variants");
            for (int index = 0; index < variants.arraySize; index++)
            {
                if (DrawVariant(variants, index)) break;
            }
            if (GUILayout.Button("Add Variant"))
            {
                variants.InsertArrayElementAtIndex(variants.arraySize);
                SerializedProperty added = variants.GetArrayElementAtIndex(variants.arraySize - 1);
                added.FindPropertyRelative("_name").stringValue = "Variant " + variants.arraySize;
                added.FindPropertyRelative("_weight").floatValue = 1f;
                added.FindPropertyRelative("_values").ClearArray();
            }
            serializedObject.ApplyModifiedProperties();
        }

        // Returns true when the variant was removed, so the caller stops drawing this frame.
        private bool DrawVariant(SerializedProperty variants, int index)
        {
            SerializedProperty variant = variants.GetArrayElementAtIndex(index);
            SerializedProperty name = variant.FindPropertyRelative("_name");
            if (!_expanded.TryGetValue(index, out bool expanded))
            {
                expanded = true;
            }
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            expanded = EditorGUILayout.Foldout(expanded, name.stringValue, true);
            _expanded[index] = expanded;
            bool removed = GUILayout.Button("Remove", EditorStyles.miniButton, GUILayout.Width(60f));
            EditorGUILayout.EndHorizontal();
            if (removed)
            {
                variants.DeleteArrayElementAtIndex(index);
                EditorGUILayout.EndVertical();
                return true;
            }
            if (expanded)
            {
                EditorGUILayout.PropertyField(name);
                EditorGUILayout.PropertyField(variant.FindPropertyRelative("_weight"));
                SerializedProperty values = variant.FindPropertyRelative("_values");
                for (int value = 0; value < values.arraySize; value++)
                {
                    if (DrawValue(values, value)) break;
                }
                DrawAddValue(values);
            }
            EditorGUILayout.EndVertical();
            return false;
        }

        // Returns true when the value was removed.
        private bool DrawValue(SerializedProperty values, int index)
        {
            SerializedProperty value = values.GetArrayElementAtIndex(index);
            SerializedProperty property = value.FindPropertyRelative("_property");
            SerializedProperty mode = value.FindPropertyRelative("_mode");
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            int current = _names.IndexOf(property.stringValue);
            var options = new List<string>(_names.Count + 1);
            foreach (string option in _names)
            {
                options.Add(Label(option));
            }
            if (current < 0)
            {
                options.Add(property.stringValue + " (not on the prototypes)");
                current = options.Count - 1;
            }
            int chosen = EditorGUILayout.Popup(current, options.ToArray());
            if (chosen != current && chosen < _names.Count)
            {
                property.stringValue = _names[chosen];
            }
            bool removed = GUILayout.Button("X", EditorStyles.miniButton, GUILayout.Width(22f));
            EditorGUILayout.EndHorizontal();
            if (removed)
            {
                values.DeleteArrayElementAtIndex(index);
                EditorGUILayout.EndVertical();
                return true;
            }
            EditorGUILayout.PropertyField(value.FindPropertyRelative("_operation"));
            EditorGUILayout.PropertyField(mode);
            bool random = mode.enumValueIndex == (int)LoogaMaterialEffector.ValueMode.Random;
            _available.TryGetValue(property.stringValue, out Available available);
            DrawVector(value.FindPropertyRelative("_value"), random ? "Minimum" : "Value", available);
            if (random)
            {
                DrawVector(value.FindPropertyRelative("_maximum"), "Maximum", available);
            }
            if (available != null && available.Prototypes < _prototypeCount)
            {
                EditorGUILayout.LabelField($"Applies to {available.Prototypes} of {_prototypeCount} prototypes.",
                    EditorStyles.miniLabel);
            }
            EditorGUILayout.EndVertical();
            return false;
        }

        private static void DrawVector(SerializedProperty vector, string label, Available available)
        {
            ShaderPropertyType type = available?.Target.Type ?? ShaderPropertyType.Vector;
            Vector4 value = vector.vector4Value;
            switch (type)
            {
                case ShaderPropertyType.Float:
                case ShaderPropertyType.Range:
                    value.x = EditorGUILayout.FloatField(label, value.x);
                    break;
                case ShaderPropertyType.Color:
                    value = EditorGUILayout.ColorField(new GUIContent(label), (Color)value, true, true, true);
                    break;
                default:
                    value = EditorGUILayout.Vector4Field(label, value);
                    break;
            }
            vector.vector4Value = value;
        }

        // A menu of the marked properties that the variant does not change yet.
        private void DrawAddValue(SerializedProperty values)
        {
            if (!GUILayout.Button("Add Value", EditorStyles.miniButton)) return;

            var used = new HashSet<string>();
            for (int index = 0; index < values.arraySize; index++)
            {
                used.Add(values.GetArrayElementAtIndex(index).FindPropertyRelative("_property").stringValue);
            }
            var menu = new GenericMenu();
            foreach (string name in _names)
            {
                if (used.Contains(name)) continue;

                string property = name;
                menu.AddItem(new GUIContent(Label(name)), false, () => AddValue(values.propertyPath, property));
            }
            if (menu.GetItemCount() == 0)
            {
                menu.AddDisabledItem(new GUIContent("No other marked property"));
            }
            menu.ShowAsContext();
        }

        private void AddValue(string valuesPath, string property)
        {
            serializedObject.Update();
            SerializedProperty values = serializedObject.FindProperty(valuesPath);
            values.InsertArrayElementAtIndex(values.arraySize);
            SerializedProperty added = values.GetArrayElementAtIndex(values.arraySize - 1);
            added.FindPropertyRelative("_property").stringValue = property;
            added.FindPropertyRelative("_operation").enumValueIndex = (int)InstanceEffectorOperation.Set;
            added.FindPropertyRelative("_mode").enumValueIndex = (int)LoogaMaterialEffector.ValueMode.Constant;
            // The new value starts at the material value of the first prototype that marks the property.
            Vector4 start = StartValue(property);
            added.FindPropertyRelative("_value").vector4Value = start;
            added.FindPropertyRelative("_maximum").vector4Value = start;
            serializedObject.ApplyModifiedProperties();
        }

        private Vector4 StartValue(string property)
        {
            if (!_available.TryGetValue(property, out Available available)) return Vector4.one;

            var stamp = ((LoogaMaterialEffector)target).GetComponent<LoogaObjectStamp>();
            foreach (ObjectPrototype prototype in stamp.Prototypes)
            {
                if (prototype == null || !prototype.Prefab) continue;

                foreach (Renderer renderer in prototype.Prefab.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (InstanceEffectorTargets.TryGet(material, available.Target.PropertyId, out _))
                        {
                            return InstanceEffectorTargets.MaterialValue(material, available.Target);
                        }
                    }
                }
            }
            return Vector4.one;
        }

        private string Label(string name)
        {
            if (!_available.TryGetValue(name, out Available available)) return name;

            string type = available.Target.Type == ShaderPropertyType.Range ? "Float" : available.Target.Type.ToString();
            return $"{available.Target.DisplayName} ({name}, {type})";
        }

        // The marked properties of the materials of the enabled stamp prototypes.
        private void CollectAvailable(LoogaObjectStamp stamp)
        {
            _available.Clear();
            _names.Clear();
            _prototypeCount = 0;
            if (!stamp) return;

            var prefabs = new HashSet<GameObject>();
            foreach (ObjectPrototype prototype in stamp.Prototypes)
            {
                if (prototype == null || !prototype.Enabled || !prototype.Prefab || !prefabs.Add(prototype.Prefab)) continue;

                _prototypeCount++;
                var marked = new HashSet<string>();
                foreach (Renderer renderer in prototype.Prefab.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (!material) continue;

                        foreach (InstanceEffectorTargets.Target found in InstanceEffectorTargets.Get(material.shader))
                        {
                            if (!marked.Add(found.Name)) continue;

                            if (!_available.TryGetValue(found.Name, out Available available))
                            {
                                available = new Available { Target = found };
                                _available.Add(found.Name, available);
                                _names.Add(found.Name);
                            }
                            available.Prototypes++;
                        }
                    }
                }
            }
        }
    }
}
