using System;
using System.Collections.Generic;
using LoogaSoft.Terrain.Surface;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Keeps the surface field map of each terrain of a stamp world that wants one, and publishes it through
    /// <see cref="LoogaTerrainSurfaceFields"/>. Maps are measured from the final heights, one terrain per step, after
    /// the heights have been quiet for a moment. Height changes update only the texels near the change.
    /// </summary>
    internal sealed class SurfaceFieldMaps : IDisposable
    {
        /// <summary>Texels per side of a map. Keep this value the same as LOOGA_SURFACE_FIELD_SIZE.</summary>
        internal const int MapSize = 512;

        // Height changes update the map this far around them, in metres. The relative heights reach 20 m. Flow can move
        // farther, but an update of the full flow reach would refill about 2 km wide areas for each edit. The field
        // filters of the stamps use the same limit.
        private const float ChangeMargin = 128f;
        private const float QuietSeconds = 0.5f;

        private sealed class Entry
        {
            internal RenderTexture Map;
            internal bool Written;
            internal bool Dirty;
            // Changed area in tile UV.
            internal Rect Region;
        }

        private readonly Dictionary<UnityEngine.Terrain, Entry> _entries = new();
        private float _lastChange = float.NegativeInfinity;
        private int _wantedVersion = -1;

        /// <summary>Terrains that wait for a map update.</summary>
        internal int Pending { get; private set; }

        /// <summary>CPU time of the last map update with the flow, in milliseconds. Timing checks read it.</summary>
        internal static double LastStepMilliseconds { get; private set; }

        public void Dispose()
        {
            foreach (KeyValuePair<UnityEngine.Terrain, Entry> pair in _entries)
            {
                if (pair.Key)
                {
                    LoogaTerrainSurfaceFields.Set(pair.Key, null);
                }
                if (!pair.Value.Map) continue;
                pair.Value.Map.Release();
                UnityEngine.Object.DestroyImmediate(pair.Value.Map);
            }
            _entries.Clear();
            Pending = 0;
        }

        /// <summary>Marks the maps near a changed world XZ rectangle.</summary>
        internal void MarkChanged(Rect worldRegion)
        {
            _lastChange = Time.realtimeSinceStartup;
            Rect grown = Rect.MinMaxRect(worldRegion.xMin - ChangeMargin, worldRegion.yMin - ChangeMargin,
                worldRegion.xMax + ChangeMargin, worldRegion.yMax + ChangeMargin);
            foreach (KeyValuePair<UnityEngine.Terrain, Entry> pair in _entries)
            {
                if (!pair.Key) continue;
                Rect tile = new StampTile(pair.Key).WorldRect;
                if (!tile.Overlaps(grown)) continue;
                Rect region = Rect.MinMaxRect(Mathf.Clamp01((grown.xMin - tile.xMin) / tile.width),
                    Mathf.Clamp01((grown.yMin - tile.yMin) / tile.height),
                    Mathf.Clamp01((grown.xMax - tile.xMin) / tile.width),
                    Mathf.Clamp01((grown.yMax - tile.yMin) / tile.height));
                Entry entry = pair.Value;
                entry.Region = entry.Dirty ? Union(entry.Region, region) : region;
                Pending += entry.Dirty ? 0 : 1;
                entry.Dirty = true;
            }
        }

        /// <summary>Updates the map of the next waiting terrain.</summary>
        internal void Step(IReadOnlyList<UnityEngine.Terrain> terrains, TerrainFieldMaps fields,
            Func<UnityEngine.Terrain, RenderTexture> heights)
        {
            if (_wantedVersion != LoogaTerrainSurfaceFields.WantedVersion)
            {
                _wantedVersion = LoogaTerrainSurfaceFields.WantedVersion;
                AddWanted(terrains);
            }
            if (Pending == 0 || Time.realtimeSinceStartup - _lastChange < QuietSeconds) return;
            foreach (KeyValuePair<UnityEngine.Terrain, Entry> pair in _entries)
            {
                Entry entry = pair.Value;
                if (!entry.Dirty) continue;
                entry.Dirty = false;
                Pending--;
                if (pair.Key)
                {
                    Write(pair.Key, entry, fields, heights);
                }
                return;
            }
        }

        private void AddWanted(IReadOnlyList<UnityEngine.Terrain> terrains)
        {
            foreach (UnityEngine.Terrain terrain in terrains)
            {
                if (!terrain || !LoogaTerrainSurfaceFields.IsWanted(terrain) || _entries.ContainsKey(terrain)) continue;
                _entries[terrain] = new Entry { Dirty = true, Region = new Rect(0f, 0f, 1f, 1f) };
                Pending++;
            }
        }

        private static void Write(UnityEngine.Terrain terrain, Entry entry, TerrainFieldMaps fields,
            Func<UnityEngine.Terrain, RenderTexture> heights)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            entry.Map = entry.Map ? entry.Map : CreateMap(terrain);
            int last = MapSize - 1;
            Rect region = entry.Written ? entry.Region : new Rect(0f, 0f, 1f, 1f);
            int minX = Mathf.Clamp(Mathf.FloorToInt(region.xMin * last), 0, last);
            int minY = Mathf.Clamp(Mathf.FloorToInt(region.yMin * last), 0, last);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(region.xMax * last) + 1, 1, MapSize);
            int maxY = Mathf.Clamp(Mathf.CeilToInt(region.yMax * last) + 1, 1, MapSize);
            var texels = new RectInt(minX, minY, maxX - minX, maxY - minY);
            if (!fields.WriteSurfaceFields(new StampTile(terrain), heights, entry.Map, texels)) return;
            entry.Map.GenerateMips();
            if (entry.Written)
            {
                LoogaTerrainSurfaceFields.Set(terrain, entry.Map, region);
            }
            else
            {
                entry.Written = true;
                LoogaTerrainSurfaceFields.Set(terrain, entry.Map);
            }
            LastStepMilliseconds = timer.Elapsed.TotalMilliseconds;
        }

        private static RenderTexture CreateMap(UnityEngine.Terrain terrain)
        {
            var descriptor = new RenderTextureDescriptor(MapSize, MapSize, GraphicsFormat.R8G8B8A8_UNorm, 0)
            {
                enableRandomWrite = true,
                useMipMap = true,
                autoGenerateMips = false
            };
            var map = new RenderTexture(descriptor)
            {
                name = "Looga Surface Fields " + terrain.name,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            map.Create();
            return map;
        }

        private static Rect Union(Rect first, Rect second)
        {
            return Rect.MinMaxRect(Mathf.Min(first.xMin, second.xMin), Mathf.Min(first.yMin, second.yMin),
                Mathf.Max(first.xMax, second.xMax), Mathf.Max(first.yMax, second.yMax));
        }
    }
}
