using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Instances
{
    /// <summary>
    /// Detail layers that another renderer draws instead of the vegetation provider, for example procedural grass.
    /// The provider places no instances for a replaced layer. Each owner sets its own layers, so two owners can
    /// replace layers of the same terrain.
    /// </summary>
    public static class ReplacedDetailLayers
    {
        private static readonly Dictionary<UnityEngine.Terrain, Dictionary<object, int[]>> Terrains = new();
        private static readonly List<UnityEngine.Terrain> Changed = new();

        /// <summary>
        /// Rises with each change of the replaced layers. A tool that leaves the replaced layers out of its own data
        /// compares it with the value of its last build.
        /// </summary>
        public static int Version { get; private set; }

        /// <summary>True when an owner draws the detail layer of the terrain.</summary>
        public static bool IsReplaced(UnityEngine.Terrain terrain, int layer)
        {
            if (!terrain || !Terrains.TryGetValue(terrain, out Dictionary<object, int[]> owners)) return false;
            foreach (int[] layers in owners.Values)
            {
                if (System.Array.IndexOf(layers, layer) >= 0) return true;
            }
            return false;
        }

        /// <summary>Sets the layers that the owner draws on the terrain. An empty list removes them.</summary>
        public static void Set(object owner, UnityEngine.Terrain terrain, int[] layers)
        {
            if (!terrain) return;
            if (!Terrains.TryGetValue(terrain, out Dictionary<object, int[]> owners))
            {
                if (layers == null || layers.Length == 0) return;
                owners = new Dictionary<object, int[]>();
                Terrains.Add(terrain, owners);
            }
            owners.TryGetValue(owner, out int[] old);
            if (layers == null || layers.Length == 0)
            {
                if (old == null) return;
                owners.Remove(owner);
                if (owners.Count == 0) Terrains.Remove(terrain);
            }
            else
            {
                if (old != null && Same(old, layers)) return;
                owners[owner] = (int[])layers.Clone();
            }
            Refresh(terrain);
        }

        /// <summary>Removes all layers of the owner.</summary>
        public static void Clear(object owner)
        {
            Changed.Clear();
            foreach (KeyValuePair<UnityEngine.Terrain, Dictionary<object, int[]>> entry in Terrains)
            {
                if (entry.Value.Remove(owner)) Changed.Add(entry.Key);
            }
            foreach (UnityEngine.Terrain terrain in Changed)
            {
                if (Terrains[terrain].Count == 0) Terrains.Remove(terrain);
                Refresh(terrain);
            }
            Changed.Clear();
        }

        private static bool Same(int[] a, int[] b)
        {
            if (a.Length != b.Length) return false;
            for (int index = 0; index < a.Length; index++)
            {
                if (a[index] != b[index]) return false;
            }
            return true;
        }

        // The provider builds its detail pages again without the replaced layers.
        private static void Refresh(UnityEngine.Terrain terrain)
        {
            Version++;
            if (terrain && terrain.TryGetComponent(out TerrainVegetationProvider provider))
            {
                provider.InvalidateAll();
            }
        }
    }
}
