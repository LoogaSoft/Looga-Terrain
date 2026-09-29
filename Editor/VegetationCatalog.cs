using System;
using System.Collections.Generic;
using LoogaSoft.Terrain.Instances;
using UnityEngine;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>Builds a source catalog without changing terrain data or creating vegetation objects.</summary>
    public static class VegetationCatalog
    {
        public sealed class Usage
        {
            public TerrainVegetationProvider Provider;
            public int PrototypeIndex;
        }

        public sealed class Entry
        {
            public VegetationKind Kind;
            public UnityEngine.Object Source;
            public readonly List<Usage> Usages = new();
            public long? SourceCount;
            public int? VisibleCount;
            public string CountLabel => Kind == VegetationKind.Tree ? "Source placements" : "Paint units";

            /// <summary>Returns unique loaded placements, including slots rejected by camera culling.</summary>
            public int GetResidentCount()
            {
                int count = 0;
                var seen = new HashSet<TerrainVegetationProvider>();
                foreach (Usage usage in Usages)
                {
                    if (usage.Provider && seen.Add(usage.Provider))
                    {
                        count += usage.Provider.GetResidentCount(Kind, Source);
                    }
                }
                return count;
            }

            /// <summary>Reads camera visibility explicitly. This operation can stall the GPU.</summary>
            public int ReadVisibleCount(Camera camera)
            {
                int count = 0;
                var seen = new HashSet<TerrainVegetationProvider>();
                foreach (Usage usage in Usages)
                {
                    if (usage.Provider && seen.Add(usage.Provider))
                    {
                        count += usage.Provider.ReadCameraVisibleCount(camera, Kind, Source);
                    }
                }
                VisibleCount = count;
                return count;
            }
        }

        /// <summary>Groups prototype slots by category and source asset, independent of slot ordering.</summary>
        public static List<Entry> Build(IEnumerable<TerrainVegetationProvider> providers)
        {
            var entries = new List<Entry>();
            var keys = new Dictionary<(VegetationKind, UnityEngine.Object), Entry>();
            var seen = new HashSet<TerrainVegetationProvider>();
            foreach (TerrainVegetationProvider provider in providers)
            {
                if (!provider || !seen.Add(provider)) continue;
                var terrain = provider.GetComponent<UnityEngine.Terrain>();
                if (!terrain || !terrain.terrainData) continue;
                var data = terrain.terrainData;
                var details = data.detailPrototypes;
                for (int i = 0; i < details.Length; i++)
                {
                    UnityEngine.Object source = details[i].usePrototypeMesh
                        ? details[i].prototype : details[i].prototypeTexture;
                    Add(VegetationKind.Detail, source, provider, i, keys, entries);
                }
                var trees = data.treePrototypes;
                for (int i = 0; i < trees.Length; i++)
                {
                    Add(VegetationKind.Tree, trees[i].prefab, provider, i, keys, entries);
                }
            }
            entries.Sort((a, b) =>
            {
                int category = a.Kind.CompareTo(b.Kind);
                return category != 0 ? category : string.Compare(a.Source.name, b.Source.name, StringComparison.OrdinalIgnoreCase);
            });
            return entries;
        }

        /// <summary>Counts source records on request. Detail paint units are not generated instance counts.</summary>
        public static bool Recount(IReadOnlyList<Entry> entries, Func<float, bool> cancel = null)
        {
            var counts = new long[entries.Count];
            for (int e = 0; e < entries.Count; e++)
            {
                Entry entry = entries[e];
                for (int u = 0; u < entry.Usages.Count; u++)
                {
                    Usage usage = entry.Usages[u];
                    if (!usage.Provider) continue;
                    var terrain = usage.Provider.GetComponent<UnityEngine.Terrain>();
                    if (!terrain || !terrain.terrainData) continue;
                    TerrainData data = terrain.terrainData;
                    int usageCount = Math.Max(1, entry.Usages.Count);
                    int entryCount = Math.Max(1, entries.Count);
                    if (cancel != null && cancel((e + (float)u / usageCount) / entryCount)) return false;
                    if (entry.Kind == VegetationKind.Tree)
                    {
                        TreeInstance[] trees = data.treeInstances;
                        for (int i = 0; i < trees.Length; i++)
                        {
                            if (i > 0 && (i & 4095) == 0 && cancel != null &&
                                cancel((e + (u + (float)i / trees.Length) / usageCount) / entryCount))
                                return false;
                            if (trees[i].prototypeIndex == usage.PrototypeIndex)
                            {
                                counts[e]++;
                            }
                        }
                    }
                    else
                    {
                        const int block = 128;
                        for (int z = 0; z < data.detailHeight; z += block)
                        {
                            float progress = (e + (u + (float)z / Math.Max(1, data.detailHeight)) /
                                usageCount) / entryCount;
                            if (cancel != null && cancel(progress)) return false;
                            for (int x = 0; x < data.detailWidth; x += block)
                            {
                                int[,] layer = data.GetDetailLayer(x, z, Math.Min(block, data.detailWidth - x),
                                    Math.Min(block, data.detailHeight - z), usage.PrototypeIndex);
                                foreach (int value in layer)
                                {
                                    counts[e] += value;
                                }
                            }
                        }
                    }
                }
            }
            for (int i = 0; i < entries.Count; i++)
            {
                entries[i].SourceCount = counts[i];
            }
            return true;
        }

        private static void Add(VegetationKind kind, UnityEngine.Object source, TerrainVegetationProvider provider,
            int index, Dictionary<(VegetationKind, UnityEngine.Object), Entry> keys, List<Entry> entries)
        {
            if (!source) return;
            var key = (kind, source);
            if (!keys.TryGetValue(key, out Entry entry))
            {
                entry = new Entry { Kind = kind, Source = source };
                keys.Add(key, entry);
                entries.Add(entry);
            }
            entry.Usages.Add(new Usage { Provider = provider, PrototypeIndex = index });
        }
    }
}
