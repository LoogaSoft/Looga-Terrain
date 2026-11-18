using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Surface
{
    /// <summary>
    /// Surface field map of each terrain: one RGBA texture over the tile with R wetness (index 2 to 15), G relative
    /// height 4 m (-2 to 2 m), B relative height 20 m (-8 to 8 m) and A log10 flow area (0 to 6). Texel i lies at tile UV
    /// i / (size - 1). A surface with active <see cref="LoogaTerrainSurface.Fields"/> reads it. A data producer, for
    /// example a stamp world, publishes the maps here. The maps are not saved.
    /// </summary>
    public static class LoogaTerrainSurfaceFields
    {
        private static readonly Dictionary<UnityEngine.Terrain, Texture> Maps = new();
        private static readonly HashSet<UnityEngine.Terrain> Wanted = new();
        private static Texture2D _neutral;

        /// <summary>Occurs when the map object of a terrain changes.</summary>
        public static event Action<UnityEngine.Terrain> MapChanged;

        /// <summary>The number of terrains whose surface reads a field map.</summary>
        public static int WantedCount => Wanted.Count;

        /// <summary>Grows each time a terrain starts or stops wanting a map.</summary>
        public static int WantedVersion { get; private set; }

        /// <summary>The map of a terrain, or a neutral map (no wetness, flat ground, no flow).</summary>
        public static Texture Get(UnityEngine.Terrain terrain)
        {
            return terrain && Maps.TryGetValue(terrain, out Texture map) && map ? map : Neutral;
        }

        /// <summary>
        /// Publishes the map of a terrain, or removes it with null, and refreshes the cached surface pages.
        /// </summary>
        /// <param name="terrain">The terrain.</param>
        /// <param name="map">The new map. It can be the same object with new content.</param>
        /// <param name="normalizedRegion">The changed area in tile UV, or null for the whole tile.</param>
        public static void Set(UnityEngine.Terrain terrain, Texture map, Rect? normalizedRegion = null)
        {
            Maps.TryGetValue(terrain, out Texture old);
            if (map)
            {
                Maps[terrain] = map;
            }
            else
            {
                Maps.Remove(terrain);
            }
            if (old != map)
            {
                MapChanged?.Invoke(terrain);
            }
            if (!terrain.TryGetComponent(out TerrainRenderer renderer)) return;
            renderer.RefreshSurface(normalizedRegion ?? new Rect(0f, 0f, 1f, 1f));
        }

        /// <summary>True when the surface of a terrain reads a field map.</summary>
        public static bool IsWanted(UnityEngine.Terrain terrain)
        {
            return Wanted.Contains(terrain);
        }

        /// <summary>Tells the producers if the surface of a terrain reads a field map.</summary>
        public static void SetWanted(UnityEngine.Terrain terrain, bool wanted)
        {
            if (wanted ? Wanted.Add(terrain) : Wanted.Remove(terrain))
            {
                WantedVersion++;
            }
        }

        private static Texture2D Neutral
        {
            get
            {
                if (_neutral) return _neutral;
                _neutral = new Texture2D(1, 1, TextureFormat.RGBA32, false, true)
                {
                    name = "Looga Neutral Surface Fields",
                    hideFlags = HideFlags.HideAndDontSave
                };
                _neutral.SetPixel(0, 0, new Color32(0, 128, 128, 0));
                _neutral.Apply(false, true);
                return _neutral;
            }
        }
    }
}
