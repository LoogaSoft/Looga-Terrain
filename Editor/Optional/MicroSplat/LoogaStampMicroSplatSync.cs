using System.Collections.Generic;
using JBooth.MicroSplat;
using LoogaSoft.Terrain.Stamps;
using UnityEditor;
using UnityEngine;

namespace LoogaSoft.Terrain.Integrations.Editor
{
    /// <summary>
    /// Keeps a MicroSplat texture array config in the terrain layer order of a Looga stamp world: the layers of the
    /// world's texture modifiers, sorted by name. MicroSplat maps terrain layer index i to array index i, so both lists
    /// must have the same order.
    /// </summary>
    /// <remarks>
    /// The sync is an explicit command. Compiling the texture arrays saves assets.
    /// </remarks>
    public static class LoogaStampMicroSplatSync
    {
        /// <summary>True when the config holds exactly the layers of the world, in the same order.</summary>
        public static bool LayersMatch(LoogaStampWorld world, TextureArrayConfig config)
        {
            if (!world || !config) return false;
            List<TerrainLayer> layers = world.GetTerrainLayers();
            if (config.sourceTextures.Count != layers.Count) return false;
            for (int i = 0; i < layers.Count; i++)
            {
                if (config.sourceTextures[i].terrainLayer != layers[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Adds missing layers to the config, removes the layers that the world does not use, sorts the entries and their
        /// MicroSplat property rows by layer name and compiles the arrays. Then gives the owned terrains the layer list
        /// and rebuilds the textures when a list changed.
        /// </summary>
        /// <returns>A short report.</returns>
        public static string Sync(LoogaStampWorld world, TextureArrayConfig config)
        {
            if (!world) throw new System.ArgumentNullException(nameof(world));
            if (!config) throw new System.ArgumentNullException(nameof(config));
            MicroSplatPropData propData = FindPropData(world) ??
                throw new MissingReferenceException("No owned terrain has a MicroSplat property data asset.");
            TerrainLayer[] layers = world.GetTerrainLayers().ToArray();
            Undo.RecordObjects(new Object[] { config, propData }, "Sync MicroSplat layers");
            TextureArrayConfigEditor.MatchAndSortTerrainLayers(config, propData, layers);
            EditorUtility.SetDirty(config);
            EditorUtility.SetDirty(propData);

            int changed = world.SyncTerrainLayers();
            foreach (UnityEngine.Terrain terrain in world.Terrains)
            {
                if (terrain && terrain.TryGetComponent(out MicroSplatTerrain surface))
                {
                    surface.Sync();
                }
            }
            if (changed > 0)
            {
                world.RebuildTextures();
            }
            return "Synchronized " + layers.Length + " layers into " + config.name + "; " + changed +
                " terrains got the new layer list.";
        }

        [MenuItem("CONTEXT/LoogaStampWorld/Sync MicroSplat Layers")]
        private static void SyncFromMenu(MenuCommand command)
        {
            var world = (LoogaStampWorld)command.context;
            if (world.LayerSyncTarget is not TextureArrayConfig config)
            {
                EditorUtility.DisplayDialog("Sync MicroSplat Layers",
                    "Assign a MicroSplat texture array config to Layer Sync Target first.", "OK");
                return;
            }
            if (LayersMatch(world, config))
            {
                Debug.Log("[Looga Stamps] " + config.name + " already matches the layers of " + world.name + ".");
                return;
            }
            Debug.Log("[Looga Stamps] " + Sync(world, config));
        }

        private static MicroSplatPropData FindPropData(LoogaStampWorld world)
        {
            foreach (UnityEngine.Terrain terrain in world.Terrains)
            {
                if (terrain && terrain.TryGetComponent(out MicroSplatTerrain surface) && surface.propData)
                {
                    return surface.propData;
                }
            }
            return null;
        }
    }
}
