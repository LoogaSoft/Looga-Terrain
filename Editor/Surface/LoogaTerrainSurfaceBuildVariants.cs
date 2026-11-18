using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace LoogaSoft.Terrain.Surface.Editor
{
    /// <summary>
    /// Before each player build, writes the saved variant materials of every Looga terrain surface. A player build
    /// keeps only the shader feature variants that saved materials use, and the surfaces create their terrain
    /// materials at runtime. Without these materials the terrain rendered black in players.
    /// </summary>
    internal sealed class LoogaTerrainSurfaceBuildVariants : IPreprocessBuildWithReport
    {
        private const string Menu = "Tools/Looga/Terrain/Update Surface Build Variants";

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            int changed = UpdateAll();
            if (changed > 0)
            {
                Debug.Log("[Looga Terrain] Updated the build variant materials of " + changed + " terrain surfaces.");
            }
        }

        [MenuItem(Menu)]
        private static void UpdateMenu()
        {
            Debug.Log("[Looga Terrain] Terrain surfaces with changed build variant materials: " + UpdateAll());
        }

        /// <summary>Updates the variant materials of all surface assets.</summary>
        /// <returns>The number of changed surfaces.</returns>
        public static int UpdateAll()
        {
            int changed = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:" + nameof(LoogaTerrainSurface)))
            {
                var surface = AssetDatabase.LoadAssetAtPath<LoogaTerrainSurface>(AssetDatabase.GUIDToAssetPath(guid));
                if (surface && surface.UpdateBuildVariants())
                {
                    changed++;
                }
            }
            if (changed > 0)
            {
                AssetDatabase.SaveAssets();
            }
            return changed;
        }
    }
}
