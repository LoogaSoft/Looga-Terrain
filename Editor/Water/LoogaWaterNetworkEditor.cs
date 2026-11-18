using System.Collections.Generic;
using System.Text;
using LoogaSoft.Inspector.Editor;
using LoogaSoft.Terrain.Water;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>
    /// Inspector summary of a water network: elements, regime lengths, falls, joints and validation issues.
    /// </summary>
    [CustomEditor(typeof(LoogaWaterNetwork))]
    internal sealed class LoogaWaterNetworkEditor : UnityEditor.Editor
    {
        private const string StateKey = "LoogaSoft.Terrain.WaterNetwork.";
        private const float BarHeight = 6f;
        private const float FrameSize = 60f;

        private readonly StringBuilder _text = new();

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();
            var network = (LoogaWaterNetwork)target;
            WaterNetworkGraph graph = network.Graph;

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Solve"))
                {
                    network.Solve();
                    SceneView.RepaintAll();
                }
                bool alwaysShow = GUILayout.Toggle(WaterNetworkOverlay.AlwaysShow,
                    new GUIContent("Always Show", "Show the debug view also when the network is not selected."),
                    "Button");
                if (alwaysShow != WaterNetworkOverlay.AlwaysShow) WaterNetworkOverlay.AlwaysShow = alwaysShow;
            }
            DrawSummary(graph);
            DrawLegend();
            LoogaGUILayout.Foldout("Channels", StateKey + "Channels", true, () => DrawChannels(graph));
            LoogaGUILayout.Foldout("Falls (" + graph.Falls.Count + ")", StateKey + "Falls", true,
                () => DrawFalls(network, graph));
            LoogaGUILayout.Foldout("Crests (" + graph.Crests.Count + ")", StateKey + "Crests", true,
                () => DrawCrests(graph));
            LoogaGUILayout.Foldout("Joints (" + graph.Connections.Count + ")", StateKey + "Joints", false,
                () => DrawJoints(graph));
            LoogaGUILayout.Foldout("Issues (" + graph.Issues.Count + ")", StateKey + "Issues", true,
                () => DrawIssues(graph));
        }

        #region Summary
        private static void DrawSummary(WaterNetworkGraph graph)
        {
            int bodies = 0;
            int channels = 0;
            foreach (WaterNetworkElement element in graph.Elements)
            {
                if (element.IsChannel)
                {
                    channels++;
                }
                else
                {
                    bodies++;
                }
            }
            int falls = 0;
            foreach (WaterNetworkConnection connection in graph.Connections)
            {
                if (connection.Fall) falls++;
            }
            EditorGUILayout.LabelField(bodies + " bodies, " + channels + " channels, " + graph.Connections.Count +
                " joints, " + falls + " free falls. Solve " + graph.SolveMilliseconds.ToString("F1") + " ms.",
                EditorStyles.miniLabel);
            int errors = graph.IssueCount(WaterIssueSeverity.Error);
            int warnings = graph.IssueCount(WaterIssueSeverity.Warning);
            if (errors + warnings == 0) return;

            EditorGUILayout.HelpBox(errors + " errors and " + warnings + " warnings. See Issues.",
                errors > 0 ? MessageType.Error : MessageType.Warning);
        }

        private static void DrawLegend()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int index = 0; index < WaterNetworkElement.RegimeCount; index++)
                {
                    var regime = (WaterRegime)index;
                    Rect swatch = GUILayoutUtility.GetRect(10f, 10f, GUILayout.Width(10f));
                    swatch.y += 4f;
                    EditorGUI.DrawRect(swatch, WaterNetworkOverlay.RegimeColor(regime));
                    GUILayout.Label(WaterNetworkOverlay.RegimeName(regime), EditorStyles.miniLabel);
                }
                GUILayout.FlexibleSpace();
            }
        }
        #endregion

        #region Lists
        private void DrawChannels(WaterNetworkGraph graph)
        {
            foreach (WaterNetworkElement element in graph.Elements)
            {
                if (!element.IsChannel) continue;

                EditorGUILayout.LabelField(element.Name, EditorStyles.boldLabel);
                DrawRegimeBar(element);
                float lowest = float.MaxValue;
                float highest = 0f;
                foreach (WaterChannelSample sample in element.Samples)
                {
                    lowest = Mathf.Min(lowest, sample.Speed);
                    highest = Mathf.Max(highest, sample.Speed);
                }
                _text.Clear();
                _text.Append(element.Length.ToString("F0")).Append(" m, Q ").Append(element.Discharge.ToString("F2"))
                    .Append(" m³/s (in ").Append(element.Inflow.ToString("F2")).Append("), V ")
                    .Append(lowest.ToString("F1")).Append(" to ").Append(highest.ToString("F1")).Append(" m/s");
                for (int index = 0; index < WaterNetworkElement.RegimeCount; index++)
                {
                    float length = element.RegimeLength((WaterRegime)index);
                    if (length < 0.5f) continue;

                    _text.Append("\n").Append(WaterNetworkOverlay.RegimeName((WaterRegime)index)).Append(" ")
                        .Append(length.ToString("F0")).Append(" m");
                }
                EditorGUILayout.LabelField(_text.ToString(), EditorStyles.wordWrappedMiniLabel);
            }
        }

        private static void DrawRegimeBar(WaterNetworkElement element)
        {
            Rect bar = GUILayoutUtility.GetRect(1f, BarHeight, GUILayout.ExpandWidth(true));
            if (element.Length <= 0f) return;

            float x = bar.x;
            for (int index = 0; index < WaterNetworkElement.RegimeCount; index++)
            {
                float width = bar.width * element.RegimeLength((WaterRegime)index) / element.Length;
                if (width <= 0f) continue;

                EditorGUI.DrawRect(new Rect(x, bar.y, width, bar.height),
                    WaterNetworkOverlay.RegimeColor((WaterRegime)index));
                x += width;
            }
        }

        private void DrawFalls(LoogaWaterNetwork network, WaterNetworkGraph graph)
        {
            if (!network.FallMaterial)
            {
                EditorGUILayout.HelpBox("Set a Fall Material to generate fall sheets. Falls with a Looga Water Fall " +
                    "component show their own sheet.", MessageType.Info);
            }
            foreach (WaterNetworkFall fall in graph.Falls)
            {
                WaterNetworkElement channel = graph.Elements[fall.Channel];
                _text.Clear();
                _text.Append(fall.Override ? "Component: " + fall.Override.name : "Generated")
                    .Append("\nDrop ").Append(fall.Drop.ToString("F1")).Append(" m, width ")
                    .Append(fall.Width.ToString("F1")).Append(" m, ").Append(fall.UnitDischarge.ToString("F2"))
                    .Append(" m²/s per metre\nLip ").Append(fall.LipSpeed.ToString("F1")).Append(" m/s, impact ")
                    .Append(fall.ImpactSpeed.ToString("F1")).Append(" m/s, plunge ")
                    .Append(fall.PlungeSpeed.ToString("F1")).Append(" m/s over ")
                    .Append(fall.PlungeDecay.ToString("F0")).Append(" m, lands ")
                    .Append(Vector2.Distance(new Vector2(fall.Lip.x, fall.Lip.z),
                        new Vector2(fall.Plunge.x, fall.Plunge.z)).ToString("F1")).Append(" m from the lip");
                EditorGUILayout.LabelField(channel.Name + " → " + graph.Elements[fall.Receiver].Name,
                    EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(_text.ToString(), EditorStyles.wordWrappedMiniLabel);
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(90f)))
                    {
                        if (GUILayout.Button("Frame")) Frame(fall.Plunge);
                        if (fall.Override)
                        {
                            if (GUILayout.Button("Select")) Selection.activeObject = fall.Override;
                        }
                        else if (GUILayout.Button(new GUIContent("Add Fall",
                                     "Adds a Looga Water Fall component at the lip, to shape this fall.")))
                        {
                            AddFall(network, channel, fall);
                        }
                    }
                }
            }
        }

        private void DrawCrests(WaterNetworkGraph graph)
        {
            if (graph.Crests.Count == 0)
            {
                EditorGUILayout.HelpBox("A Looga Water Fall with the Body Crest source marks a lip line where an " +
                    "area body spills over its edge.", MessageType.Info);
            }
            foreach (WaterNetworkCrest crest in graph.Crests)
            {
                string receiver = crest.Receiver >= 0 ? graph.Elements[crest.Receiver].Name : "no water below";
                _text.Clear();
                _text.Append("Drop ").Append(crest.Drop.ToString("F1")).Append(" m, lip ")
                    .Append(crest.Length.ToString("F0")).Append(" m, ").Append(crest.UnitDischarge.ToString("F2"))
                    .Append(" m²/s per metre (").Append(crest.DesignDischarge ? "design" : "inflow")
                    .Append(")\nLip ").Append(crest.LipSpeed.ToString("F1")).Append(" m/s, depth ")
                    .Append(crest.LipDepth.ToString("F2")).Append(" m, impact ")
                    .Append(crest.ImpactSpeed.ToString("F1")).Append(" m/s");
                EditorGUILayout.LabelField(graph.Elements[crest.Body].Name + " → " + receiver, EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(_text.ToString(), EditorStyles.wordWrappedMiniLabel);
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(90f)))
                    {
                        if (GUILayout.Button("Frame")) Frame(crest.Lip[crest.Lip.Count / 2]);
                        if (GUILayout.Button("Select")) Selection.activeObject = crest.Fall;
                    }
                }
            }
        }

        // The new fall object stands at the lip and faces the flow, with the network fall settings.
        private static void AddFall(LoogaWaterNetwork network, WaterNetworkElement channel, WaterNetworkFall fall)
        {
            var gameObject = new GameObject("Fall - " + channel.Name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Add Water Fall");
            gameObject.layer = channel.Body.gameObject.layer;
            gameObject.transform.SetParent(channel.Body.transform, false);
            gameObject.transform.SetPositionAndRotation(fall.Lip,
                Quaternion.LookRotation(new Vector3(fall.Direction.x, 0f, fall.Direction.y)));
            var component = gameObject.AddComponent<LoogaWaterFall>();
            component.Settings.CopyFrom(network.FallSettings);
            component.SetMaterials(network.FallMaterial, null);
            component.SetChannel(channel.Body, channel.SplineIndex);
            Selection.activeGameObject = gameObject;
        }

        private static void DrawJoints(WaterNetworkGraph graph)
        {
            IReadOnlyList<WaterNetworkElement> elements = graph.Elements;
            foreach (WaterNetworkConnection connection in graph.Connections)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(elements[connection.Upstream].Name + " → " +
                        elements[connection.Downstream].Name + "\n" + connection.Joint + ", drop " +
                        connection.Drop.ToString("F2") + " m" + (connection.Fall ? ", free fall" : ""),
                        EditorStyles.wordWrappedMiniLabel);
                    if (GUILayout.Button("Frame", GUILayout.Width(50f))) Frame(connection.Point);
                }
            }
        }

        private static void DrawIssues(WaterNetworkGraph graph)
        {
            IReadOnlyList<WaterNetworkElement> elements = graph.Elements;
            foreach (WaterNetworkIssue issue in graph.Issues)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string name = issue.Element >= 0 ? elements[issue.Element].Name + ": " : string.Empty;
                    EditorGUILayout.HelpBox(name + issue.Message, MessageTypeOf(issue.Severity));
                    if (GUILayout.Button("Frame", GUILayout.Width(50f))) Frame(issue.Position);
                }
            }
        }

        private static MessageType MessageTypeOf(WaterIssueSeverity severity)
        {
            return severity switch
            {
                WaterIssueSeverity.Error => MessageType.Error,
                WaterIssueSeverity.Warning => MessageType.Warning,
                _ => MessageType.Info
            };
        }

        private static void Frame(Vector3 position)
        {
            SceneView view = SceneView.lastActiveSceneView;
            if (!view) return;

            view.Frame(new Bounds(position, Vector3.one * FrameSize), false);
        }
        #endregion
    }
}
