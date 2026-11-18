using System.Collections.Generic;
using LoogaSoft.Inspector.Editor;
using LoogaSoft.Terrain.Geology;
using LoogaSoft.Terrain.Instances;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>
    /// Shows terrain work as the "Looga Terrain" task in the editor progress bar: rock face builds, terrain tiles that
    /// initialize or draw natively, and vegetation that loads or fell back to native foliage.
    /// </summary>
    [InitializeOnLoad]
    internal static class LoogaTerrainActivity
    {
        private static readonly List<UnityEngine.Terrain> _terrains = new();

        static LoogaTerrainActivity()
        {
            LoogaActivity.Register("Looga Terrain", AddEntries);
        }

        private static void AddEntries(List<string> entries)
        {
            int rockFaces = LoogaRockFace.PendingCount;
            if (rockFaces > 0)
            {
                entries.Add("Rock faces: building " + rockFaces);
            }

            UnityEngine.Terrain.GetActiveTerrains(_terrains);
            int initializing = 0;
            int native = 0;
            string nativeReason = null;
            int loading = 0;
            int nativeFoliage = 0;
            string foliageReason = null;
            foreach (UnityEngine.Terrain terrain in _terrains)
            {
                if (!terrain) continue;
                if (terrain.TryGetComponent(out TerrainRenderer renderer) && renderer.isActiveAndEnabled &&
                    !renderer.IsReady)
                {
                    if (string.IsNullOrEmpty(renderer.Diagnostic))
                    {
                        initializing++;
                    }
                    else
                    {
                        native++;
                        nativeReason ??= renderer.Diagnostic;
                    }
                }
                if (!terrain.TryGetComponent(out TerrainVegetationProvider provider) ||
                    !provider.isActiveAndEnabled) continue;
                // Without the editor preview, native foliage in edit mode is the expected state.
                bool expectsLooga = Application.isPlaying || provider.EditorPreviewActive;
                if (!expectsLooga) continue;
                if (terrain.drawTreesAndFoliage && !provider.IsRendering)
                {
                    nativeFoliage++;
                    foliageReason ??= provider.Diagnostic;
                }
                // After a script reload, native foliage stays hidden while the vegetation loads.
                else if (!provider.IsRendering || provider.LastMissingRequiredCells > 0)
                {
                    loading++;
                }
            }
            if (initializing > 0)
            {
                entries.Add("Terrain: initializing " + initializing + " tiles");
            }
            if (native > 0)
            {
                entries.Add("Terrain: " + native + " tiles drawn natively (" + nativeReason + ")");
            }
            if (loading > 0)
            {
                entries.Add("Vegetation: loading cells on " + loading + " terrains");
            }
            if (nativeFoliage > 0)
            {
                entries.Add("Vegetation: " + nativeFoliage + " terrains on native foliage, slow" +
                    (string.IsNullOrEmpty(foliageReason) ? "" : " (" + foliageReason + ")"));
            }
        }
    }
}
