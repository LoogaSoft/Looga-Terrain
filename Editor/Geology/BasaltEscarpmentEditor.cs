using LoogaSoft.Inspector.Editor;
using LoogaSoft.Terrain.Geology;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>
    /// Inspector for basalt pillar groups. The main settings are in short sections. Rarely used settings are in
    /// the collapsed Advanced section.
    /// </summary>
    [CustomEditor(typeof(BasaltEscarpment))]
    public sealed class BasaltEscarpmentEditor : UnityEditor.Editor
    {
        private const string FoldoutKey = "LoogaSoft.Terrain.BasaltEscarpment.";
        private const float RadiusSliderMaximum = 40f;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox("The spline marks the top edge of the wall. Pillars fill rows on the right of " +
                                    "the spline direction. Enabling the component builds only the preview window.",
                MessageType.Info);

            LoogaGUILayout.Foldout("Spline", FoldoutKey + "Spline", true, DrawSpline);
            LoogaGUILayout.Foldout("Pillars", FoldoutKey + "Pillars", true, DrawPillars);
            LoogaGUILayout.Foldout("Height", FoldoutKey + "Height", true, DrawHeight);
            LoogaGUILayout.Foldout("Wall", FoldoutKey + "Wall", true, DrawWall);
            LoogaGUILayout.Foldout("Surface", FoldoutKey + "Surface", false, DrawSurface);
            LoogaGUILayout.Foldout("Advanced", FoldoutKey + "Advanced", false, DrawAdvanced);
            serializedObject.ApplyModifiedProperties();

            DrawActions((BasaltEscarpment)target);
        }

        #region Sections
        private void DrawSpline()
        {
            Field("_spline");
            Field("_autoPreviewOnEnable", "Preview On Enable");
            Field("_generateWholeSpline", "Always Whole Spline");
            using (new EditorGUI.DisabledScope(serializedObject.FindProperty("_generateWholeSpline").boolValue))
            {
                Field("_previewCenter");
                Field("_previewLength");
            }
        }

        private void DrawPillars()
        {
            RadiusRange(serializedObject.FindProperty("_layout.Radius"));
            Field("_layout.EdgeRadiusScale", "Edge Radius Scale");
            Field("_layout.EdgeWidth", "Edge Width");
            Field("_layout.RadiusJitter", "Radius Jitter");
            SidesRange();
            Field("_layout.LeanDegrees", "Lean");
        }

        private void DrawHeight()
        {
            SerializedProperty relative = serializedObject.FindProperty("_layout.UseRelativeHeight");
            LoogaGUILayout.PropertyField(relative, new GUIContent("Relative To Cliff",
                "Pillar heights follow the cliff height below the spline. Off = the fixed Height range."));
            if (relative.boolValue)
            {
                Field("_layout.MinimumEscarpmentHeight", "Lowest Share");
                Field("_layout.MaximumHeightAboveEscarpment", "Rise Above Top");
            }
            else
            {
                LoogaGUILayout.PropertyField(serializedObject.FindProperty("_layout.Height._range"),
                    new GUIContent("Height", "Pillar height range, in meters."));
            }
            LoogaGUILayout.PropertyField(serializedObject.FindProperty("_layout.Height._distribution"),
                new GUIContent("Height Distribution", "Maps a random value from 0 to 1 to a position in the height " +
                    "range. A curve that rises fast puts most pillars near the top of the range."));
            Field("_layout.TopVariation", "Top Variation");
            Field("_layout.EndHeightScale", "End Height Scale");
            Field("_layout.OuterRowHeightScale", "Outer Row Height");
        }

        private void DrawWall()
        {
            Field("_layout.WallDepth", "Depth");
            Field("_layout.BottomDepth", "Base Below Spline");
            Field("_clampBasesToTerrain", "Stand On Terrain");
            Field("_layout.Density");
            Field("_layout.FillGaps", "Fill Gaps");
            Field("_layout.BoundaryNoise", "Outline Noise");
            Field("_layout.EndTaperLength", "End Taper");
            Field("_layout.ReverseOutward", "Flip Side");
            Field("_layout.Seed");
        }

        private void DrawSurface()
        {
            Field("_material");
            Field("_materialProfile");
            Field("_maximumDistance", "Draw Distance");
            Field("_verticalSegments");
            Field("_edgeWear");
            Field("_surfaceRoughness");
            Field("_crackDepth");
        }

        private void DrawAdvanced()
        {
            Field("_layout.JointGap", "Joint Gap");
            Field("_layout.MinimumCenterSeparation", "Minimum Separation");
            Field("_layout.RadiusNoiseScale", "Radius Noise Scale");
            Field("_layout.BoundaryNoiseScale", "Outline Noise Scale");
            Field("_layout.BoundaryJitter", "Outline Jitter");
            Field("_layout.OuterRowDropout", "Outer Row Dropout");
            Field("_layout.EndDropout", "End Dropout");
            Field("_layout.StartDensityScale", "Start Density");
            Field("_layout.StartDensityFadeLength", "Start Density Fade");
            Field("_layout.EndDensityScale", "End Density");
            Field("_layout.EndDensityFadeLength", "End Density Fade");
            Field("_layout.MaximumHeightScale", "Maximum Height Scale");
            Field("_layout.Height._distribution", "Height Distribution");
            Field("_layout.ShapeVariants", "Shape Variants");
        }
        #endregion

        #region Fields
        private void Field(string path, string label = null)
        {
            SerializedProperty property = serializedObject.FindProperty(path);
            if (property == null) return;

            if (label == null)
            {
                LoogaGUILayout.PropertyField(property, true);
            }
            else
            {
                LoogaGUILayout.PropertyField(property, new GUIContent(label, property.tooltip), true);
            }
        }

        private static void RadiusRange(SerializedProperty radius)
        {
            Vector2 value = radius.vector2Value;
            float minimum = Mathf.Min(value.x, value.y);
            float maximum = Mathf.Max(value.x, value.y);
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("Radius", radius.tooltip));
                minimum = EditorGUILayout.FloatField(minimum, GUILayout.Width(48f));
                EditorGUILayout.MinMaxSlider(ref minimum, ref maximum, 0.5f, RadiusSliderMaximum);
                maximum = EditorGUILayout.FloatField(maximum, GUILayout.Width(48f));
            }
            if (EditorGUI.EndChangeCheck())
            {
                minimum = Mathf.Max(0.05f, minimum);
                radius.vector2Value = new Vector2(minimum, Mathf.Max(minimum, maximum));
            }
        }

        private void SidesRange()
        {
            SerializedProperty minimumSides = serializedObject.FindProperty("_layout.MinimumSides");
            SerializedProperty maximumSides = serializedObject.FindProperty("_layout.MaximumSides");
            float minimum = minimumSides.intValue;
            float maximum = maximumSides.intValue;
            EditorGUI.BeginChangeCheck();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("Sides", "Number of pillar sides, from 5 to 8."));
                EditorGUILayout.LabelField(minimumSides.intValue.ToString(), GUILayout.Width(20f));
                EditorGUILayout.MinMaxSlider(ref minimum, ref maximum, 5f, 8f);
                EditorGUILayout.LabelField(maximumSides.intValue.ToString(), GUILayout.Width(20f));
            }
            if (EditorGUI.EndChangeCheck())
            {
                minimumSides.intValue = Mathf.RoundToInt(minimum);
                maximumSides.intValue = Mathf.Max(minimumSides.intValue, Mathf.RoundToInt(maximum));
            }
        }
        #endregion

        #region Actions
        private static void DrawActions(BasaltEscarpment escarpment)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Generated Pillars", escarpment.PlacementCount.ToString());
            EditorGUILayout.LabelField("Shared Mesh Variants", escarpment.PrototypeCount.ToString());
            if (!string.IsNullOrEmpty(escarpment.Diagnostic))
                EditorGUILayout.HelpBox(escarpment.Diagnostic, MessageType.Warning);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Build Local Preview"))
                {
                    escarpment.RebuildPreview();
                    SceneView.RepaintAll();
                }
                if (GUILayout.Button("Clear Preview"))
                {
                    escarpment.ClearPreview();
                    SceneView.RepaintAll();
                }
            }
            if (GUILayout.Button("Build Whole Spline"))
            {
                if (EditorUtility.DisplayDialog("Build the full basalt wall?",
                    "This may generate thousands of GPU instances. The operation remains reversible with Clear Preview.",
                    "Build", "Cancel"))
                {
                    escarpment.RebuildWholeSpline();
                    SceneView.RepaintAll();
                }
            }
        }

        [MenuItem("GameObject/Looga/Create Basalt Escarpment", false, 11)]
        private static void Create(MenuCommand menuCommand)
        {
            GameObject gameObject = new("Basalt Escarpment");
            Undo.RegisterCreatedObjectUndo(gameObject, "Create basalt escarpment");
            GameObjectUtility.SetParentAndAlign(gameObject, menuCommand.context as GameObject);
            BasaltEscarpment escarpment = gameObject.AddComponent<BasaltEscarpment>();
            SplineContainer spline = gameObject.AddComponent<SplineContainer>();
            spline.Spline.Add(new BezierKnot(new Unity.Mathematics.float3(-25f, 0f, 0f)));
            spline.Spline.Add(new BezierKnot(new Unity.Mathematics.float3(25f, 0f, 0f)));
            escarpment.ConfigureSpline(spline);
            Selection.activeObject = escarpment;
        }
        #endregion
    }
}
