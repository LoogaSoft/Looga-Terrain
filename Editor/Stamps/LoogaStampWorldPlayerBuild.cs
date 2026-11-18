using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps.Editor
{
    /// <summary>
    /// Saves the current stamp result into the terrain data before a player build. A player reads the saved heights,
    /// alphamaps and placements of the terrain data.
    /// </summary>
    /// <remarks>
    /// The stamp world writes the heights and the alphamaps on the GPU. It copies them to the CPU when the scene saves.
    /// An asset save without a scene save, for example the save at the start of a build or a save of a scene that has
    /// no changes, writes the old CPU copies. After an editor start the CPU copies hold the saved files, so such a save
    /// can keep an old result for any number of builds (Kubera World 2026-11-15: rock and scree patches of the live
    /// cliffs were missing in the player). This step stops the build while a stamp world updates, then waits for the
    /// placements, copies the heights and the alphamaps to the CPU and saves the terrain data of each world.
    /// </remarks>
    internal sealed class LoogaStampWorldPlayerBuild : IPreprocessBuildWithReport
    {
        // Before the generated mesh step (-100), whose generators read the heights.
        public int callbackOrder => -200;

        public void OnPreprocessBuild(BuildReport report)
        {
            LoogaStampWorld[] worlds = Object.FindObjectsByType<LoogaStampWorld>(FindObjectsSortMode.None);
            var updating = new List<string>();
            foreach (LoogaStampWorld world in worlds)
            {
                if (world.IsUpdating) updating.Add(world.name);
            }
            if (updating.Count > 0)
            {
                throw new BuildFailedException("[Looga Terrain] The stamp worlds " + string.Join(", ", updating) +
                    " still update. Start the build again when they are idle.");
            }

            int saved = 0;
            foreach (LoogaStampWorld world in worlds)
            {
                world.WaitForPendingPlacements();
                world.WaitForPendingHeights();
                world.SynchronizeHeights();
                world.SynchronizeTextures();
                foreach (UnityEngine.Terrain terrain in world.Terrains)
                {
                    if (!terrain || !terrain.terrainData || !EditorUtility.IsDirty(terrain.terrainData)) continue;

                    AssetDatabase.SaveAssetIfDirty(terrain.terrainData);
                    saved++;
                }
            }
            if (saved > 0)
            {
                Debug.Log("[Looga Terrain] Saved the stamp result of " + saved + " terrains for the player build.");
            }
        }
    }
}
