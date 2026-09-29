using System;
using System.Collections.Generic;

namespace LoogaSoft.Terrain
{
    /// <summary>
    /// Allocates bounded physical pages. Recently requested pages cannot evict each other during the same feedback epoch.
    /// </summary>
    public sealed class VirtualPagePool
    {
        private readonly Dictionary<ulong, int> _slots = new();
        private readonly ulong[] _keys;
        private readonly long[] _used;
        private readonly bool[] _occupied;
        private long _epoch = 1;

        /// <summary>Gets the current resident page count.</summary>
        public int Count => _slots.Count;
        /// <summary>Gets the fixed page capacity.</summary>
        public int Capacity => _keys.Length;

        /// <summary>Creates a fixed-capacity pool.</summary>
        public VirtualPagePool(int capacity)
        {
            if (capacity < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }
            _keys = new ulong[capacity];
            _used = new long[capacity];
            _occupied = new bool[capacity];
        }

        /// <summary>Starts a new feedback epoch.</summary>
        public void BeginEpoch()
        {
            _epoch++;
        }

        /// <summary>Marks a resident page as used in the current epoch.</summary>
        public bool Touch(ulong key)
        {
            if (!_slots.TryGetValue(key, out int slot)) return false;
            _used[slot] = _epoch;
            return true;
        }

        /// <summary>Gets a resident slot, or minus one when the page is absent.</summary>
        public int Find(ulong key)
        {
            return _slots.TryGetValue(key, out int slot) ? slot : -1;
        }

        /// <summary>Acquires a slot. Returns an evicted key when replacement occurs.</summary>
        public int Acquire(ulong key, out ulong? evicted)
        {
            evicted = null;
            if (_slots.TryGetValue(key, out int existing))
            {
                _used[existing] = _epoch;
                return existing;
            }
            int candidate = -1;
            long oldest = _epoch;
            for (int slot = 0; slot < _keys.Length; slot++)
            {
                if (!_occupied[slot])
                {
                    candidate = slot;
                    break;
                }
                if (_used[slot] < oldest)
                {
                    oldest = _used[slot];
                    candidate = slot;
                }
            }
            if (candidate < 0) return -1;
            if (_occupied[candidate])
            {
                evicted = _keys[candidate];
                _slots.Remove(_keys[candidate]);
            }
            _keys[candidate] = key;
            _used[candidate] = _epoch;
            _occupied[candidate] = true;
            _slots.Add(key, candidate);
            return candidate;
        }

        /// <summary>Releases one resident page without changing other page slots.</summary>
        public bool Remove(ulong key)
        {
            if (!_slots.Remove(key, out int slot)) return false;
            _occupied[slot] = false;
            return true;
        }

        /// <summary>Releases every page owned by one tile.</summary>
        public void RemoveTile(uint tile)
        {
            for (int slot = 0; slot < _keys.Length; slot++)
            {
                if (!_occupied[slot] || (uint)(_keys[slot] >> 32) != tile)
                {
                    continue;
                }
                _slots.Remove(_keys[slot]);
                _occupied[slot] = false;
            }
        }

        /// <summary>Combines a tile identifier and page table index.</summary>
        public static ulong Key(uint tile, int page)
        {
            return ((ulong)tile << 32) | (uint)page;
        }
    }
}
