using LoogaSoft.Instancing;
using LoogaSoft.Terrain.Instances;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Editor
{
    internal static class VegetationQualityGUI
    {
        internal static bool Draw(ref InstanceQualitySettings quality, bool overrides, ref VegetationQualityFields fields)
        {
            EditorGUI.BeginChangeCheck();
            quality.ViewDistance = Float("View distance (m)", "Zero inherits the native terrain distance.",
                VegetationQualityFields.ViewDistance, ref fields, overrides, quality.ViewDistance);
            quality.ViewFadeDistance = Float("View fade band (m)", "Fade width before the view limit. Unsupported shaders use stable instance thinning.",
                VegetationQualityFields.ViewFadeDistance, ref fields, overrides, quality.ViewFadeDistance);
            quality.LodBias = Float("LOD bias", "Zero inherits the original LOD behavior. Larger values retain finer meshes farther away.",
                VegetationQualityFields.LodBias, ref fields, overrides, quality.LodBias);
            BeginRow(VegetationQualityFields.Decorative, ref fields, overrides);
            quality.Decorative = EditorGUILayout.Toggle(new GUIContent("Decorative population", "Required for density reduction. Sources with gameplay colliders cannot be reduced."), quality.Decorative);
            EndRow();
            BeginRow(VegetationQualityFields.Density, ref fields, overrides);
            quality.Density = EditorGUILayout.Slider("Visible density", quality.Density, 0, 1);
            EndRow();
            quality.MinimumPixels = Float("Minimum size (pixels)", "Zero disables projected-size rejection. Requires a decorative population.",
                VegetationQualityFields.MinimumPixels, ref fields, overrides, quality.MinimumPixels);
            quality.DensityFalloffDistance = Float("Density falloff start (m)", "Zero disables distance thinning. Requires a decorative population.",
                VegetationQualityFields.DensityFalloffDistance, ref fields, overrides, quality.DensityFalloffDistance);
            BeginRow(VegetationQualityFields.FarDensity, ref fields, overrides);
            quality.FarDensity = EditorGUILayout.Slider(new GUIContent("Density at view limit", "Fraction of visible density kept at the view distance."), quality.FarDensity, 0, 1);
            EndRow();
            EditorGUILayout.Space(3);
            BeginRow(VegetationQualityFields.ShadowMode, ref fields, overrides);
            quality.ShadowMode = (InstanceShadowMode)EditorGUILayout.EnumPopup("Cast shadows", quality.ShadowMode);
            EndRow();
            quality.ShadowDistance = Float("Shadow distance (m)", "Zero inherits the provider limit. URP and the provider cap may reduce the effective distance.",
                VegetationQualityFields.ShadowDistance, ref fields, overrides, quality.ShadowDistance);
            quality.ShadowFadeDistance = Float("Shadow fade band (m)", "Stable instance thinning over this distance before the shadow limit.",
                VegetationQualityFields.ShadowFadeDistance, ref fields, overrides, quality.ShadowFadeDistance);
            BeginRow(VegetationQualityFields.MinimumShadowLod, ref fields, overrides);
            quality.MinimumShadowLod = EditorGUILayout.IntSlider("Minimum shadow LOD", quality.MinimumShadowLod, 0, 7);
            EndRow();
            BeginRow(VegetationQualityFields.ShadowSplits, ref fields, overrides);
            quality.ShadowSplits = EditorGUILayout.IntSlider(new GUIContent("Shadow cascades", "Zero permits every directional cascade."), quality.ShadowSplits, 0, 16);
            EndRow();
            return EditorGUI.EndChangeCheck();
        }

        private static float Float(string label, string tip, VegetationQualityFields field,
            ref VegetationQualityFields fields, bool overrides, float value)
        {
            BeginRow(field, ref fields, overrides);
            float result = EditorGUILayout.FloatField(new GUIContent(label, tip), value);
            EndRow();
            return result;
        }

        private static void BeginRow(VegetationQualityFields field, ref VegetationQualityFields fields, bool overrides)
        {
            EditorGUILayout.BeginHorizontal();
            bool enabled = !overrides || (fields & field) != 0;
            if (overrides)
            {
                enabled = EditorGUILayout.Toggle(new GUIContent("", "Override this field. Clear to inherit the category default."), enabled, GUILayout.Width(18));
                fields = enabled ? fields | field : fields & ~field;
            }
            EditorGUI.BeginDisabledGroup(!enabled);
        }

        private static void EndRow()
        {
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
        }
    }
}
