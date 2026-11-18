using System.Collections.Generic;
using LoogaSoft.Terrain.Stamps;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Surface.Editor
{
    /// <summary>
    /// Updates the ground cover producers in the area of each Looga stamp placement update, so the ground cover follows
    /// detail stamp edits. The updater listens to every enabled stamp world while a producer is enabled.
    /// </summary>
    [InitializeOnLoad]
    internal static class LoogaGroundCoverUpdater
    {
        private static readonly HashSet<LoogaStampWorld> Worlds = new();

        static LoogaGroundCoverUpdater()
        {
            // The enabled world list is short and reading it makes no garbage, so each tick can check it without a
            // search of the scenes.
            EditorApplication.update += Subscribe;
        }

        private static void Subscribe()
        {
            if (LoogaGroundCoverProducer.Active.Count == 0) return;

            IReadOnlyList<LoogaStampWorld> enabled = LoogaStampWorld.EnabledWorlds;
            for (int index = 0; index < enabled.Count; index++)
            {
                LoogaStampWorld world = enabled[index];
                if (Worlds.Add(world))
                {
                    world.PlacementsUpdated += OnPlacementsUpdated;
                }
            }
            if (Worlds.Count > enabled.Count)
            {
                Worlds.RemoveWhere(world => !world);
            }
        }

        private static void OnPlacementsUpdated(Rect region)
        {
            foreach (LoogaGroundCoverProducer producer in LoogaGroundCoverProducer.Active)
            {
                producer.MarkDirty(region);
            }
        }
    }
}
