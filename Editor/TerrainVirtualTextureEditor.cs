using UnityEditor;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>Shows shared virtual cache use and an explicit refresh action.</summary>
    [CustomEditor(typeof(TerrainVirtualTexture)), CanEditMultipleObjects]
    public sealed class TerrainVirtualTextureEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var component = (TerrainVirtualTexture)target;
            if (!string.IsNullOrEmpty(component.Diagnostic))
            {
                EditorGUILayout.HelpBox(component.Diagnostic, MessageType.Warning);
            }
            VirtualTextureStatistics statistics = component.Statistics;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Shared cache", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Resident pages", $"{statistics.ResidentPages} / {statistics.Capacity}");
            EditorGUILayout.LabelField("Pool sized for", TerrainVirtualTexture.PoolPixels > 0
                ? $"{TerrainVirtualTexture.PoolPixels / 1000000.0:F2} megapixels" : "reference resolution");
            EditorGUILayout.LabelField("Physical surfaces", $"{statistics.CacheBytes / 1048576.0:F1} MiB");
            EditorGUILayout.LabelField("Cache storage", statistics.Compressed ? "BC3" : "Uncompressed");
            if (statistics.StagingBytes > 0)
            {
                EditorGUILayout.LabelField("Encoder workspace", $"{statistics.StagingBytes / 1048576.0:F2} MiB");
            }
            EditorGUILayout.LabelField("Pending pages", statistics.PendingPages.ToString());
            EditorGUILayout.LabelField("Residency mip bias", statistics.ResidencyMipBias.ToString("F2"));
            EditorGUILayout.LabelField("Page fills / evictions", $"{statistics.PageFills} / {statistics.Evictions}");
            if (UnityEngine.GUILayout.Button("Refresh cached surfaces"))
            {
                foreach (UnityEngine.Object item in targets)
                {
                    ((TerrainVirtualTexture)item).Invalidate();
                }
            }
        }
    }
}
