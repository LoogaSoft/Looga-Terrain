using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// GPU fields for each owner and terrain tile, with a memory budget. The least recently used fields go first. The
    /// cache owns the textures that it holds.
    /// </summary>
    internal sealed class TileFieldCache : IDisposable
    {
        private struct Key : IEquatable<Key>
        {
            internal int Owner;
            internal int Terrain;

            public bool Equals(Key other) => Owner == other.Owner && Terrain == other.Terrain;
            public override bool Equals(object other) => other is Key key && Equals(key);
            public override int GetHashCode() => Owner * 397 ^ Terrain;
        }

        private sealed class Entry
        {
            internal RenderTexture Texture;
            internal Rect TileRect;
            internal long Bytes;
            internal long LastUse;
        }

        private readonly Dictionary<Key, Entry> _entries = new();
        private readonly List<Key> _removed = new();
        private long _useCounter;

        internal TileFieldCache(long budget)
        {
            Budget = budget;
        }

        /// <summary>Memory limit in bytes.</summary>
        internal long Budget { get; set; }

        /// <summary>GPU memory of the cached fields, in bytes.</summary>
        internal long CachedBytes { get; private set; }

        /// <summary>Fields added since the last ResetStatistics call.</summary>
        internal int BuildCount { get; private set; }

        /// <summary>Fields taken from the cache since the last ResetStatistics call.</summary>
        internal int CacheHits { get; private set; }

        public void Dispose()
        {
            Clear();
        }

        internal void ResetStatistics()
        {
            BuildCount = 0;
            CacheHits = 0;
        }

        internal bool TryGet(UnityEngine.Object owner, in StampTile tile, out RenderTexture texture)
        {
            if (_entries.TryGetValue(KeyOf(owner, tile), out Entry entry) && entry.Texture)
            {
                entry.LastUse = ++_useCounter;
                texture = entry.Texture;
                CacheHits++;
                return true;
            }
            texture = null;
            return false;
        }

        /// <summary>Stores a new field and takes ownership of its texture. The newest field always stays.</summary>
        internal void Add(UnityEngine.Object owner, in StampTile tile, RenderTexture texture)
        {
            BuildCount++;
            Key key = KeyOf(owner, tile);
            Remove(key);
            long bytes = (long)texture.width * texture.height * GraphicsFormatUtility.GetBlockSize(texture.graphicsFormat);
            _entries[key] = new Entry { Texture = texture, TileRect = tile.WorldRect, Bytes = bytes, LastUse = ++_useCounter };
            CachedBytes += bytes;
            Trim(key);
        }

        /// <summary>Removes the fields of one owner on the tiles that a world rectangle touches, or on all tiles.</summary>
        internal void Invalidate(UnityEngine.Object owner, Rect? region = null)
        {
            int id = owner.GetInstanceID();
            _removed.Clear();
            foreach (KeyValuePair<Key, Entry> pair in _entries)
            {
                if (pair.Key.Owner != id) continue;
                if (!region.HasValue || StampMapping.Touches(pair.Value.TileRect, region.Value))
                {
                    _removed.Add(pair.Key);
                }
            }
            foreach (Key key in _removed)
            {
                Remove(key);
            }
        }

        internal void Clear()
        {
            foreach (Entry entry in _entries.Values)
            {
                Release(entry.Texture);
            }
            _entries.Clear();
            CachedBytes = 0;
        }

        private static Key KeyOf(UnityEngine.Object owner, in StampTile tile)
        {
            return new Key { Owner = owner.GetInstanceID(), Terrain = tile.Terrain.GetInstanceID() };
        }

        // Removes the least recently used fields until the cache fits the budget.
        private void Trim(Key keep)
        {
            while (CachedBytes > Budget && _entries.Count > 1)
            {
                Key oldest = keep;
                long oldestUse = long.MaxValue;
                foreach (KeyValuePair<Key, Entry> pair in _entries)
                {
                    if (pair.Key.Equals(keep) || pair.Value.LastUse >= oldestUse) continue;
                    oldest = pair.Key;
                    oldestUse = pair.Value.LastUse;
                }
                if (oldest.Equals(keep)) return;
                Remove(oldest);
            }
        }

        private void Remove(Key key)
        {
            if (!_entries.TryGetValue(key, out Entry entry)) return;
            CachedBytes -= entry.Bytes;
            Release(entry.Texture);
            _entries.Remove(key);
        }

        private static void Release(RenderTexture texture)
        {
            if (!texture) return;
            texture.Release();
            UnityEngine.Object.DestroyImmediate(texture);
        }
    }
}
