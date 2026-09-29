using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using JBooth.MicroSplat;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Integrations.Editor
{
    /// <summary>Reports the checked feature scope without converting terrain or changing vendor assets.</summary>
    [CustomEditor(typeof(MicroSplatTerrainBridge))]
    public sealed class MicroSplatCompatibilityEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var bridge = (MicroSplatTerrainBridge)target;
            if (!string.IsNullOrEmpty(bridge.Diagnostic))
            {
                EditorGUILayout.HelpBox(bridge.Diagnostic, MessageType.Warning);
            }
            if (GUILayout.Button("Synchronize Material"))
            {
                bridge.SynchronizeMaterial();
                bridge.GetComponent<TerrainRenderer>().RefreshCompatibility();
            }
            if (GUILayout.Button("Inspect Source Compatibility"))
            {
                ShowReport(bridge.GetComponent<MicroSplatTerrain>());
            }
        }

        [MenuItem("Tools/Looga/Terrain/Inspect Selected Compatibility")]
        private static void InspectSelected()
        {
            ShowReport(Selection.activeGameObject ? Selection.activeGameObject.GetComponent<MicroSplatTerrain>() : null);
        }

        private static void ShowReport(MicroSplatTerrain source)
        {
            if (!source || !source.matInstance)
            {
                EditorUtility.DisplayDialog("Looga Terrain Compatibility", "Select a terrain with a synchronized MicroSplat material.", "OK");
                return;
            }
            string path = AssetDatabase.GetAssetPath(source.matInstance.shader);
            string code = File.ReadAllText(path);
            string unsupported = MicroSplatCompatibility.GetUnsupported(ReadFeatures(code));
            string status = string.IsNullOrEmpty(unsupported)
                ? "The shader features fit the direct surface scope. The cache exporter checks a separate, smaller feature set. Visual checks are still required."
                : "Native rendering is required by the current policy.\nUnverified features: " + unsupported;
            if (!code.Contains("MicroSplat Version: 3.9") || !code.Contains("#define _URP 1"))
            {
                status = "Unsupported generator layout. MicroSplat 3.9 URP is required.\n" + status;
            }
            var renderer = source.GetComponent<TerrainRenderer>();
            string mode = renderer && renderer.IsReady ? "Looga renderer active" : "Native or external renderer";
            EditorUtility.DisplayDialog("Looga Terrain Compatibility",
                mode + "\n" + status + "\n\nGraphics API: " + SystemInfo.graphicsDeviceType
                + "\nD3D12 is tested. D3D11 has an unresolved indirect-buffer issue."
                + "\n\nSource: " + path, "OK");
        }

        /// <summary>Reads enabled generated features in a deterministic order.</summary>
        public static string[] ReadFeatures(string code)
        {
            int start = code.IndexOf("#define _MICROSPLAT 1", StringComparison.Ordinal);
            int end = start < 0 ? -1 : code.IndexOf("#pragma instancing_options", start, StringComparison.Ordinal);
            if (start < 0 || end < start)
            {
                throw new NotSupportedException("The MicroSplat feature block is absent.");
            }
            var features = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(code.Substring(start, end - start),
                @"#define\s+(\w+)\s+1", RegexOptions.None, TimeSpan.FromSeconds(1)))
            {
                features.Add(match.Groups[1].Value);
            }
            // MicroSplat emits legacy URP markers beside the current URP 6 marker.
            if (features.Contains("_MSRENDERLOOP_UNITYURP6"))
            {
                features.ExceptWith(new[] { "_MSRENDERLOOP_UNITYLD", "_MSRENDERLOOP_UNITYURP2020",
                    "_MSRENDERLOOP_UNITYURP2021", "_MSRENDERLOOP_UNITYURP2022", "_MSRENDERLOOP_UNITYURP6P3" });
            }
            var result = new string[features.Count];
            features.CopyTo(result);
            return result;
        }
    }
}
