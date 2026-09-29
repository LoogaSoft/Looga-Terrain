using LoogaSoft.Instancing;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Instances
{
    // Previous geometry is separate from surface RVT. Only native height changes require a new copy.
    internal sealed class TerrainGroundHistory : System.IDisposable
    {
        private sealed class Entry
        {
            internal RenderTexture Current;
            internal RenderTexture Previous;
            internal int Frame = -1;
            internal bool Dirty;
            internal RenderTexture Source;
            internal uint Revision;
            internal int UsedFrame;
        }
        private readonly Dictionary<TerrainData, Entry> _entries = new();
        private readonly List<TerrainData> _expired = new();

        internal TerrainGroundHistory() => TerrainCallbacks.heightmapChanged += Changed;
        private void Changed(UnityEngine.Terrain terrain, RectInt region, bool synchronized)
        {
            if (terrain && terrain.terrainData && _entries.TryGetValue(terrain.terrainData, out var entry))
            {
                entry.Dirty = true;
            }
        }
        internal Texture Previous(TerrainData data, int frame)
        {
            if (!_entries.TryGetValue(data, out var entry))
            {
                entry = new Entry { Current = Copy(data.heightmapTexture), Source = data.heightmapTexture, Revision = data.heightmapTexture.updateCount };
                entry.Previous = entry.Current;
                _entries.Add(data, entry);
            }
            entry.UsedFrame = frame;
            if (entry.Frame == frame) return entry.Previous;
            entry.Frame = frame;
            if (entry.Previous != entry.Current)
            {
                Release(entry.Previous);
            }
            entry.Previous = entry.Current;
            if (entry.Dirty || entry.Source != data.heightmapTexture || entry.Revision != data.heightmapTexture.updateCount ||
                entry.Current.width != data.heightmapTexture.width)
            {
                entry.Current = Copy(data.heightmapTexture);
                entry.Dirty = false;
                entry.Source = data.heightmapTexture;
                entry.Revision = data.heightmapTexture.updateCount;
            }
            return entry.Previous;
        }
        internal void RemoveUnused(int frame)
        {
            _expired.Clear();
            foreach (var pair in _entries)
            {
                if (!pair.Key || pair.Value.UsedFrame != frame)
                {
                    _expired.Add(pair.Key);
                }
            }
            foreach (var key in _expired)
            {
                Release(_entries[key]);
                _entries.Remove(key);
            }
        }
        private static RenderTexture Copy(RenderTexture source)
        {
            var texture = new RenderTexture(source.descriptor)
            {
                hideFlags = HideFlags.HideAndDontSave, name = "Looga previous terrain height"
            };
            try
            {
                if (!texture.Create())
                {
                    throw new System.InvalidOperationException("The terrain height-history texture could not be allocated.");
                }
                Graphics.CopyTexture(source, texture);
                return texture;
            }
            catch
            {
                Release(texture);
                throw;
            }
        }
        private static void Release(RenderTexture texture)
        {
            if (!texture) return;
            if (Application.isPlaying) Object.Destroy(texture);
            else Object.DestroyImmediate(texture);
        }
        private static void Release(Entry entry)
        {
            if (entry.Previous != entry.Current)
            {
                Release(entry.Previous);
            }
            Release(entry.Current);
        }
        public void Dispose()
        {
            TerrainCallbacks.heightmapChanged -= Changed;
            foreach (var entry in _entries.Values)
            {
                Release(entry);
            }
            _entries.Clear();
        }
    }
}
