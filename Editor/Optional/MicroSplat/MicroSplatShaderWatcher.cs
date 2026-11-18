using System;
using UnityEditor;

namespace LoogaSoft.Terrain.Integrations.Editor
{
    /// <summary>
    /// Tells the MicroSplat bridges about imported shaders. A shader import can change shader tags and names
    /// without a new shader reference.
    /// </summary>
    internal sealed class MicroSplatShaderWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved,
            string[] movedFrom)
        {
            foreach (string path in imported)
            {
                if (!path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)
                    && !path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)) continue;
                MicroSplatTerrainBridge.MarkShadersChanged();
                return;
            }
        }
    }
}
