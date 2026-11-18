using System.Collections.Generic;
using LoogaSoft.Terrain.Water;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>
    /// Scene view debug view of a <see cref="LoogaWaterNetwork"/>. Each channel shows the colour of its regime. Labels
    /// show the speed, the slope, the discharge and the Froude number. Joints, fall paths, plunges and validation
    /// issues have markers. The view shows when the network is selected, or always with the Always Show option.
    /// </summary>
    internal static class WaterNetworkOverlay
    {
        private const string AlwaysShowKey = "LoogaSoft.Terrain.WaterNetwork.AlwaysShow";
        // Largest camera distance to an element that the view draws, in metres.
        private const float ViewDistance = 5000f;
        // Largest camera distance of the value labels, in metres.
        private const float LabelDistance = 500f;
        // Largest camera distance of the name, joint and issue labels, in metres.
        private const float NameDistance = 2500f;
        // Largest camera distance of the body labels, in metres. Many small bodies fill a far view with labels.
        private const float BodyNameDistance = 1200f;
        // Distance between the value labels along a channel, in metres.
        private const float LabelSpacing = 50f;
        // Height of the lines above the water surface, in metres.
        private const float Lift = 0.3f;
        private const float ChannelLineWidth = 5f;
        private const float OutlineLineWidth = 2f;
        private const float FallLineWidth = 8f;

        private static readonly Color[] RegimeColors =
        {
            new(0.55f, 0.65f, 0.75f),
            new(0.2f, 0.5f, 1f),
            new(0f, 0.85f, 0.9f),
            new(1f, 0.85f, 0.1f),
            new(1f, 0.45f, 0.1f),
            new(1f, 0.1f, 0.65f)
        };
        private static readonly string[] RegimeNames = { "Still", "Flowing", "Fast", "Rapids", "Cascade", "Free fall" };
        private static readonly Color ErrorColor = new(1f, 0.2f, 0.2f);
        private static readonly Color WarningColor = new(1f, 0.7f, 0.2f);
        private static readonly Color InfoColor = new(0.8f, 0.8f, 0.8f);

        private static Vector3[] _line = new Vector3[512];
        private static int _lineCount;
        private static readonly List<Vector3> FallPath = new();
        // Networks of the open scenes for Always Show. A hierarchy change clears it.
        private static LoogaWaterNetwork[] _allNetworks;
        private static GUIStyle _labelStyle;
        private static Texture2D _labelBackground;

        /// <summary>True when the view also shows networks that are not selected.</summary>
        internal static bool AlwaysShow
        {
            get => EditorPrefs.GetBool(AlwaysShowKey, false);
            set
            {
                EditorPrefs.SetBool(AlwaysShowKey, value);
                _allNetworks = null;
                SceneView.RepaintAll();
            }
        }

        internal static Color RegimeColor(WaterRegime regime)
        {
            return RegimeColors[(int)regime];
        }

        internal static string RegimeName(WaterRegime regime)
        {
            return RegimeNames[(int)regime];
        }

        internal static Color IssueColor(WaterIssueSeverity severity)
        {
            return severity switch
            {
                WaterIssueSeverity.Error => ErrorColor,
                WaterIssueSeverity.Warning => WarningColor,
                _ => InfoColor
            };
        }

        // The view does not use gizmos, so it also shows when the Scene view gizmos are off.
        [InitializeOnLoadMethod]
        private static void Subscribe()
        {
            SceneView.duringSceneGui += OnSceneGui;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
        }

        private static void OnHierarchyChanged()
        {
            _allNetworks = null;
        }

        private static void OnSceneGui(SceneView sceneView)
        {
            if (Event.current.type != EventType.Repaint || !sceneView.camera) return;

            Vector3 eye = sceneView.camera.transform.position;
            if (AlwaysShow)
            {
                _allNetworks ??= Object.FindObjectsByType<LoogaWaterNetwork>(FindObjectsSortMode.None);
                foreach (LoogaWaterNetwork network in _allNetworks)
                {
                    if (network && network.isActiveAndEnabled) DrawNetwork(network, eye);
                }
                return;
            }
            foreach (GameObject selected in Selection.gameObjects)
            {
                if (selected.TryGetComponent(out LoogaWaterNetwork network) && network.isActiveAndEnabled)
                {
                    DrawNetwork(network, eye);
                }
            }
        }

        private static void DrawNetwork(LoogaWaterNetwork network, Vector3 eye)
        {
            WaterNetworkGraph graph = network.Graph;
            CompareFunction zTest = Handles.zTest;
            Color color = Handles.color;
            Handles.zTest = CompareFunction.Always;
            foreach (WaterNetworkElement element in graph.Elements)
            {
                if (FlatDistance(element.Bounds, eye) > ViewDistance) continue;

                if (element.IsChannel)
                {
                    DrawChannel(element, eye);
                }
                else
                {
                    DrawBody(element, graph, eye);
                }
            }
            DrawJoints(graph, eye);
            DrawFalls(graph, eye);
            DrawCrests(graph, eye);
            DrawIssues(graph, eye);
            Handles.zTest = zTest;
            Handles.color = color;
        }

        #region Elements
        private static void DrawChannel(WaterNetworkElement element, Vector3 eye)
        {
            var samples = element.Samples;
            if (samples.Count < 2) return;

            _lineCount = 0;
            WaterRegime regime = samples[0].Regime;
            float nextLabel = 0f;
            foreach (WaterChannelSample sample in samples)
            {
                Vector3 point = sample.Position + Vector3.up * Lift;
                // A regime change ends the run at this point and starts the next run here, so the line has no gap.
                if (sample.Regime != regime)
                {
                    AddPoint(point);
                    FlushLine(RegimeColor(regime), ChannelLineWidth);
                    regime = sample.Regime;
                }
                AddPoint(point);
                if (sample.Station < nextLabel) continue;

                nextLabel = (Mathf.Floor(sample.Station / LabelSpacing) + 1f) * LabelSpacing;
                if (Vector3.Distance(point, eye) < LabelDistance) Label(point + Vector3.up * 2f, SampleText(sample));
            }
            FlushLine(RegimeColor(regime), ChannelLineWidth);

            WaterChannelSample middle = samples[samples.Count / 2];
            if (Vector3.Distance(middle.Position, eye) < NameDistance)
            {
                Label(middle.Position + Vector3.up * 6f, element.Name + "\nQ " + element.Discharge.ToString("F2") +
                    " m³/s, " + element.Length.ToString("F0") + " m");
            }
        }

        private static void DrawBody(WaterNetworkElement element, WaterNetworkGraph graph, Vector3 eye)
        {
            Color color = RegimeColor(WaterRegime.Still);
            color.a = 0.7f;
            foreach (var outline in element.Outlines)
            {
                _lineCount = 0;
                foreach (Vector2 point in outline)
                {
                    AddPoint(new Vector3(point.x, element.Level + Lift, point.y));
                }
                // Close the outline.
                AddPoint(new Vector3(outline[0].x, element.Level + Lift, outline[0].y));
                FlushLine(color, OutlineLineWidth);
            }
            Vector3 centre = new(element.Bounds.center.x, element.Level, element.Bounds.center.y);
            if (Vector3.Distance(centre, eye) > BodyNameDistance) return;

            int inflows = 0;
            int outflows = 0;
            int index = IndexOf(graph, element);
            foreach (WaterNetworkConnection connection in graph.Connections)
            {
                if (connection.Downstream == index) inflows++;
                if (connection.Upstream == index) outflows++;
            }
            Label(centre + Vector3.up * 4f, element.Name + "\nStill, level " + element.Level.ToString("F2") +
                " m, in " + inflows + ", out " + outflows);
        }

        private static void DrawJoints(WaterNetworkGraph graph, Vector3 eye)
        {
            foreach (WaterNetworkConnection connection in graph.Connections)
            {
                float distance = Vector3.Distance(connection.Point, eye);
                if (distance > ViewDistance) continue;

                Vector3 point = connection.Point + Vector3.up * Lift;
                if (connection.Fall && HasFall(graph, connection))
                {
                    // The fall path shows the drop.
                }
                else if (connection.Fall)
                {
                    Vector3 landing = connection.Landing + Vector3.up * Lift;
                    Handles.color = RegimeColor(WaterRegime.FreeFall);
                    Handles.DrawAAPolyLine(FallLineWidth, point, landing);
                    Handles.DrawWireDisc(landing, Vector3.up, Mathf.Max(2f, 0.5f * connection.Width));
                }
                else
                {
                    Handles.color = Color.white;
                    Handles.DrawWireDisc(point, Vector3.up, Mathf.Max(1.5f, 0.5f * connection.Width));
                }
                if (distance > NameDistance) continue;

                string text = connection.Joint + ", drop " + connection.Drop.ToString("F2") + " m";
                if (connection.Fall) text = "Free fall " + connection.Drop.ToString("F1") + " m\n" + text;
                Label(point + Vector3.up * 3f, text);
            }
        }

        // The sheet centreline from the lip to the run-out end, and the plunge across the fall width.
        private static void DrawFalls(WaterNetworkGraph graph, Vector3 eye)
        {
            foreach (WaterNetworkFall fall in graph.Falls)
            {
                float distance = Vector3.Distance(fall.Plunge, eye);
                if (distance > ViewDistance) continue;

                fall.GetPath(FallPath);
                Handles.color = RegimeColor(WaterRegime.FreeFall);
                if (FallPath.Count > 1) Handles.DrawAAPolyLine(FallLineWidth, FallPath.ToArray());
                Vector3 across = new Vector3(-fall.Direction.y, 0f, fall.Direction.x) * (0.5f * fall.Width);
                Vector3 plunge = fall.Plunge + Vector3.up * Lift;
                Handles.DrawAAPolyLine(ChannelLineWidth, plunge - across, plunge + across);
                Handles.DrawWireDisc(plunge, Vector3.up, Mathf.Max(2f, 0.5f * fall.Width));
                if (distance > NameDistance) continue;

                Label(plunge + Vector3.up * 3f, "Fall " + fall.Drop.ToString("F1") + " m, impact " +
                    fall.ImpactSpeed.ToString("F1") + " m/s\nPlunge " + fall.PlungeSpeed.ToString("F1") + " m/s, q " +
                    fall.UnitDischarge.ToString("F2") + " m²/s" + (fall.Override ? ", component" : ", generated"));
            }
        }

        // The lip line, the landing line and a drop line every few lip points.
        private static void DrawCrests(WaterNetworkGraph graph, Vector3 eye)
        {
            foreach (WaterNetworkCrest crest in graph.Crests)
            {
                int middle = crest.Lip.Count / 2;
                float distance = Vector3.Distance(crest.Lip[middle], eye);
                if (distance > ViewDistance) continue;

                Handles.color = RegimeColor(WaterRegime.FreeFall);
                FallPath.Clear();
                foreach (Vector3 point in crest.Lip)
                {
                    FallPath.Add(point + Vector3.up * Lift);
                }
                Handles.DrawAAPolyLine(FallLineWidth, FallPath.ToArray());
                FallPath.Clear();
                foreach (Vector3 point in crest.Landing)
                {
                    FallPath.Add(point + Vector3.up * Lift);
                }
                Handles.DrawAAPolyLine(ChannelLineWidth, FallPath.ToArray());
                int step = Mathf.Max(1, crest.Lip.Count / 8);
                for (int index = 0; index < crest.Lip.Count; index += step)
                {
                    Handles.DrawAAPolyLine(ChannelLineWidth, crest.Lip[index], crest.Landing[index]);
                }
                if (distance > NameDistance) continue;

                string receiver = crest.Receiver >= 0 ? graph.Elements[crest.Receiver].Name : "no water below";
                Label(crest.Lip[middle] + Vector3.up * 4f, "Crest " + crest.Fall.name + " → " + receiver + "\nDrop " +
                    crest.Drop.ToString("F1") + " m, lip " + crest.Length.ToString("F0") + " m, q " +
                    crest.UnitDischarge.ToString("F2") + " m²/s (" + (crest.DesignDischarge ? "design" : "inflow") +
                    ")\nLip " + crest.LipSpeed.ToString("F1") + " m/s, depth " + crest.LipDepth.ToString("F2") +
                    " m, impact " + crest.ImpactSpeed.ToString("F1") + " m/s");
            }
        }

        private static bool HasFall(WaterNetworkGraph graph, WaterNetworkConnection connection)
        {
            foreach (WaterNetworkFall fall in graph.Falls)
            {
                WaterNetworkConnection other = graph.Connections[fall.Connection];
                if (other.Upstream == connection.Upstream && other.Downstream == connection.Downstream) return true;
            }
            return false;
        }

        private static void DrawIssues(WaterNetworkGraph graph, Vector3 eye)
        {
            foreach (WaterNetworkIssue issue in graph.Issues)
            {
                float distance = Vector3.Distance(issue.Position, eye);
                if (distance > ViewDistance) continue;

                Handles.color = IssueColor(issue.Severity);
                Handles.DrawWireDisc(issue.Position + Vector3.up * Lift, Vector3.up, 4f);
                Handles.DrawAAPolyLine(3f, issue.Position, issue.Position + Vector3.up * 8f);
                if (distance > NameDistance) continue;

                Label(issue.Position + Vector3.up * 9f, issue.Severity + ": " + issue.Message);
            }
        }
        #endregion

        #region Drawing
        private static string SampleText(WaterChannelSample sample)
        {
            return RegimeName(sample.Regime) + "  V " + sample.Speed.ToString("F1") + " m/s\nS " +
                (sample.Slope * 100f).ToString("F1") + " %  Q " + sample.Discharge.ToString("F1") + " m³/s  Fr " +
                sample.Froude.ToString("F2") + "\nTurbulence " + sample.Turbulence.ToString("F2") + "  Aeration " +
                sample.Aeration.ToString("F2");
        }

        private static void AddPoint(Vector3 point)
        {
            if (_lineCount == _line.Length) System.Array.Resize(ref _line, _line.Length * 2);
            _line[_lineCount++] = point;
        }

        private static void FlushLine(Color color, float width)
        {
            if (_lineCount >= 2)
            {
                Handles.color = color;
                Handles.DrawAAPolyLine(width, _lineCount, _line);
            }
            _lineCount = 0;
        }

        private static void Label(Vector3 position, string text)
        {
            if (_labelStyle == null || !_labelBackground)
            {
                _labelBackground = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
                _labelBackground.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.6f));
                _labelBackground.Apply();
                _labelStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    normal = { textColor = Color.white, background = _labelBackground },
                    padding = new RectOffset(4, 4, 2, 2)
                };
            }
            Handles.Label(position, text, _labelStyle);
        }

        private static int IndexOf(WaterNetworkGraph graph, WaterNetworkElement element)
        {
            for (int index = 0; index < graph.Elements.Count; index++)
            {
                if (graph.Elements[index] == element) return index;
            }
            return -1;
        }

        private static float FlatDistance(Rect rect, Vector3 point)
        {
            float x = Mathf.Max(rect.xMin - point.x, 0f, point.x - rect.xMax);
            float z = Mathf.Max(rect.yMin - point.z, 0f, point.z - rect.yMax);
            return Mathf.Sqrt(x * x + z * z);
        }
        #endregion
    }
}
