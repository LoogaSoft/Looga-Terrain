using System.Collections.Generic;
using LoogaSoft.Inspector.Editor;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps.Editor
{
    /// <summary>Shows the stamp world updates as the "Looga Stamps" task in the editor progress bar.</summary>
    [InitializeOnLoad]
    internal static class LoogaStampActivity
    {
        static LoogaStampActivity()
        {
            LoogaActivity.Register("Looga Stamps", AddEntries);
        }

        private static void AddEntries(List<string> entries)
        {
            foreach (LoogaStampWorld world in LoogaStampWorld.Enabled)
            {
                if (!world || !world.UpdateInEditor) continue;
                string prefix = LoogaStampWorld.Enabled.Count > 1 ? "Stamps (" + world.name + "): " : "Stamps: ";
                if (world.HasRequestedUpdate)
                {
                    entries.Add(prefix + "update requested");
                }
                if (world.PlacementsPending)
                {
                    Vector2Int progress = world.PlacementProgress;
                    entries.Add(progress.x < progress.y
                        ? prefix + "placing objects and details, terrain " + progress.x + " of " + progress.y
                        : prefix + "reading back placements");
                }
                else if (world.IsUpdating)
                {
                    entries.Add(prefix + (world.HeightChecksPending ? "writing heights and textures" : "updating"));
                }
                else if (world.HeightChecksPending)
                {
                    entries.Add(prefix + "checking height writes");
                }
                int unsynchronized = world.UnsynchronizedHeightCount;
                if (unsynchronized > 0 && !world.IsUpdating)
                {
                    entries.Add(prefix + "synchronizing CPU heights, " + unsynchronized + " terrains left");
                }
            }
        }
    }
}
