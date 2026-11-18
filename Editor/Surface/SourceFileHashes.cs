using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace LoogaSoft.Terrain.Surface.Editor
{
    /// <summary>
    /// Content hashes of source files for the array builder. The hash of a file is computed again only when its size
    /// or write date changes. The table is kept in the Library folder, so an editor reload does not read the files again.
    /// A content hash is the same on every computer, so arrays that a teammate built are not built again.
    /// </summary>
    internal static class SourceFileHashes
    {
        private const string TablePath = "Library/LoogaTerrainSurface/SourceHashes.txt";

        private readonly struct Entry
        {
            public Entry(long length, long writeTicks, Hash128 hash)
            {
                Length = length;
                WriteTicks = writeTicks;
                Hash = hash;
            }

            public long Length { get; }
            public long WriteTicks { get; }
            public Hash128 Hash { get; }
        }

        private static Dictionary<string, Entry> _entries;
        private static bool _changed;

        /// <summary>Returns the content hash of a file. A missing file gives the default hash.</summary>
        /// <param name="path">File system path of the file.</param>
        public static Hash128 Get(string path)
        {
            Load();
            var info = new FileInfo(path);
            if (!info.Exists) return default;
            long ticks = info.LastWriteTimeUtc.Ticks;
            if (_entries.TryGetValue(path, out Entry entry) && entry.Length == info.Length && entry.WriteTicks == ticks)
            {
                return entry.Hash;
            }
            Hash128 hash = Hash128.Compute(File.ReadAllBytes(path));
            _entries[path] = new Entry(info.Length, ticks, hash);
            _changed = true;
            return hash;
        }

        /// <summary>Writes the table when a hash changed.</summary>
        public static void Save()
        {
            if (!_changed) return;
            var text = new StringBuilder();
            foreach (KeyValuePair<string, Entry> pair in _entries)
            {
                text.Append(pair.Key).Append('\t').Append(pair.Value.Length.ToString(CultureInfo.InvariantCulture))
                    .Append('\t').Append(pair.Value.WriteTicks.ToString(CultureInfo.InvariantCulture))
                    .Append('\t').Append(pair.Value.Hash.ToString()).Append('\n');
            }
            Directory.CreateDirectory(Path.GetDirectoryName(TablePath));
            File.WriteAllText(TablePath, text.ToString());
            _changed = false;
        }

        private static void Load()
        {
            if (_entries != null) return;
            _entries = new Dictionary<string, Entry>();
            if (!File.Exists(TablePath)) return;
            foreach (string line in File.ReadAllLines(TablePath))
            {
                string[] fields = line.Split('\t');
                if (fields.Length != 4) continue;
                if (!long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long length)) continue;
                if (!long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)) continue;
                _entries[fields[0]] = new Entry(length, ticks, Hash128.Parse(fields[3]));
            }
        }
    }
}
