using System;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Integrations.Editor
{
    /// <summary>Exports a Graphics Pro writer from an existing MicroSplat surface producer.</summary>
    public sealed class MicroSplatClipmapExportWindow : EditorWindow
    {
        private Material _pageProducer;
        private Material _nativeMaterial;

        [MenuItem("Tools/Looga Terrain/Export Graphics Pro Clipmap Writer")]
        private static void Open()
        {
            GetWindow<MicroSplatClipmapExportWindow>("Clipmap Writer");
        }

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("Use the existing Looga Terrain page producer and its matching native MicroSplat material.", MessageType.Info);
            _pageProducer = (Material)EditorGUILayout.ObjectField("Page Producer", _pageProducer, typeof(Material), false);
            _nativeMaterial = (Material)EditorGUILayout.ObjectField("Native Material", _nativeMaterial, typeof(Material), false);
            using (new EditorGUI.DisabledScope(!_pageProducer || !_nativeMaterial))
            {
                if (!GUILayout.Button("Export Writer"))
                    return;
                string path = EditorUtility.SaveFilePanelInProject("Export Clipmap Writer", "ClipmapWriter", "shader", "Choose a new asset path.");
                if (string.IsNullOrEmpty(path))
                    return;
                try
                {
                    Material writer = MicroSplatClipmapAdapter.Export(_pageProducer, _nativeMaterial.shader, path.Substring(0, path.Length - 7));
                    Selection.activeObject = writer;
                    EditorGUIUtility.PingObject(writer);
                }
                catch (Exception exception)
                {
                    EditorUtility.DisplayDialog("Clipmap Export", exception.Message, "OK");
                }
            }
        }
    }
}
