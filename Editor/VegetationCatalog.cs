using System;
using System.Collections.Generic;
using LoogaSoft.Instancing;
using LoogaSoft.Terrain.Instances;
using UnityEngine;

namespace LoogaSoft.Terrain.Editor
{
    /// <summary>Builds a source catalog without changing terrain data or creating vegetation objects.</summary>
    public static class VegetationCatalog
    {
        public sealed class Usage
        {
            /// <summary>Terrain that uses a Detail or Tree source. Null for an Object source.</summary>
            public TerrainVegetationProvider Provider;
            /// <summary>Terrain prototype slot of a Detail or Tree source.</summary>
            public int PrototypeIndex;
            /// <summary>InstanceSet or InstanceContainer that places an Object source. Null for terrain sources.</summary>
            public Component Owner;
        }

        public sealed class Entry
        {
            public VegetationKind Kind;
            public UnityEngine.Object Source;
            public readonly List<Usage> Usages = new();
            public long? SourceCount;
            public int? VisibleCount;
            public string CountLabel => GetCountLabel(Kind);

            /// <summary>Returns unique loaded placements, including slots rejected by camera culling.</summary>
            public int GetResidentCount()
            {
                if (Kind == VegetationKind.Object) return CountObjectPlacements(this, true);
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

        /// <summary>Returns the name of the source count of one category.</summary>
        public static string GetCountLabel(VegetationKind kind)
        {
            switch (kind)
            {
                case VegetationKind.Detail:
                    return "Paint units";
                case VegetationKind.Tree:
                    return "Source placements";
                default:
                    return "Placements";
            }
        }

        /// <summary>Groups terrain prototype slots by category and source asset, independent of slot ordering.</summary>
        public static List<Entry> Build(IEnumerable<TerrainVegetationProvider> providers)
        {
            return Build(providers, Array.Empty<InstanceSet>(), Array.Empty<InstanceContainer>());
        }

        /// <summary>
        /// Groups terrain prototype slots and instance prototypes by category and source asset. Object entries get
        /// their placement counts at once, because the sets and containers store them.
        /// </summary>
        public static List<Entry> Build(IEnumerable<TerrainVegetationProvider> providers, IEnumerable<InstanceSet> sets,
            IEnumerable<InstanceContainer> containers)
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
            var prototypes = new List<GameObject>();
            foreach (InstanceSet set in sets)
            {
                if (!set) continue;
                prototypes.Clear();
                set.GetPrototypes(prototypes);
                foreach (GameObject prototype in prototypes)
                {
                    AddObject(prototype, set, keys, entries);
                }
            }
            foreach (InstanceContainer container in containers)
            {
                if (container) AddObject(container.Prototype, container, keys, entries);
            }
            foreach (Entry entry in entries)
            {
                if (entry.Kind == VegetationKind.Object)
                {
                    entry.SourceCount = CountObjectPlacements(entry, false);
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
                if (entry.Kind == VegetationKind.Object)
                {
                    counts[e] = CountObjectPlacements(entry, false);
                    continue;
                }
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

        // Resident placements are the placements of an active owner with a renderer.
        private static int CountObjectPlacements(Entry entry, bool residentOnly)
        {
            var prototype = entry.Source as GameObject;
            int count = 0;
            foreach (Usage usage in entry.Usages)
            {
                if (usage.Owner is InstanceSet set && set)
                {
                    if (!residentOnly || (set.isActiveAndEnabled && set.Renderer != null))
                    {
                        count += set.CountPlacements(prototype);
                    }
                }
                else if (usage.Owner is InstanceContainer container && container)
                {
                    if (!residentOnly || (container.isActiveAndEnabled && container.Renderer != null))
                    {
                        count += container.PlacementCount;
                    }
                }
            }
            return count;
        }

        private static void AddObject(GameObject prototype, Component owner,
            Dictionary<(VegetationKind, UnityEngine.Object), Entry> keys, List<Entry> entries)
        {
            Add(VegetationKind.Object, prototype, null, -1, keys, entries, owner);
        }

        private static void Add(VegetationKind kind, UnityEngine.Object source, TerrainVegetationProvider provider,
            int index, Dictionary<(VegetationKind, UnityEngine.Object), Entry> keys, List<Entry> entries,
            Component owner = null)
        {
            if (!source) return;
            var key = (kind, source);
            if (!keys.TryGetValue(key, out Entry entry))
            {
                entry = new Entry { Kind = kind, Source = source };
                keys.Add(key, entry);
                entries.Add(entry);
            }
            entry.Usages.Add(new Usage { Provider = provider, PrototypeIndex = index, Owner = owner });
        }
    }
}
