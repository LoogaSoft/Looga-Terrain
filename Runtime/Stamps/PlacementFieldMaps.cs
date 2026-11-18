using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// The placements that the placement fields of a tile read: for each field source, the placements of the current
    /// evaluation on its terrains, else the stored result of the world.
    /// </summary>
    internal sealed class PlacementFieldSources
    {
        internal readonly List<LoogaObjectStamp> Stamps = new();
        internal StampPlacements Current;
        internal readonly HashSet<UnityEngine.Terrain> CurrentTerrains = new();
        internal StampPlacements Stored;

        /// <summary>The placements of a source on a terrain, one list for each prototype, or null when it has none.</summary>
        internal List<StampPlacements.Placement>[] Get(LoogaObjectStamp stamp, UnityEngine.Terrain terrain)
        {
            if (Current != null && Current.Contains(stamp) && CurrentTerrains.Contains(terrain))
            {
                return Current.GetLists(stamp, terrain);
            }
            return Stored?.GetLists(stamp, terrain);
        }
    }

    /// <summary>
    /// Builds the placement field map of one tile on the CPU: R = <see cref="TerrainField.Canopy"/>, G =
    /// <see cref="TerrainField.ObjectFootDistance"/> in metres. The map comes from the placements of the field sources on
    /// the tile and around it. Texel centres are at tile UV (i + 0.5) / size, like the other field maps. The map stays
    /// valid until the next build or <see cref="Dispose"/>.
    /// </summary>
    internal sealed class PlacementFieldMaps : IDisposable
    {
        /// <summary>Largest foot distance, in metres. The field has this value where no footprint is nearer.</summary>
        internal const float FootReach = 12f;

        /// <summary>
        /// Distance in metres that a field source change moves the fields: the foot reach and a large crown. An update of
        /// a source grows its region by this distance.
        /// </summary>
        internal const float UpdateMargin = 32f;

        // A crown is full inside CanopyInner x radius and ends at CanopyOuter x radius.
        private const float CanopyInner = 0.5f;
        private const float CanopyOuter = 1.2f;
        // Field texels are about this size, in metres.
        private const float TargetCell = 1f;
        // Share of the mesh height from the bottom whose vertices give the footprint radius.
        private const float FootprintBand = 0.25f;
        // Largest height of the footprint band above the bottom of the mesh, in prefab units.
        private const float FootprintHeight = 2f;

        // Measured radii of the prefabs in prefab units: x crown, y footprint. A prefab edit takes effect after a domain
        // reload.
        private readonly Dictionary<GameObject, Vector2> _radii = new();
        // True when the editor refused a vertex read since the last TakeRefusedRead. See TryReadVertices.
        private static bool _refusedRead;
        private readonly List<UnityEngine.Terrain> _near = new();
        private Texture2D _map;

        /// <summary>CPU time of the last build, in milliseconds. Timing checks read it.</summary>
        internal static double LastBuildMilliseconds { get; private set; }

        /// <summary>Placements that the last build read.</summary>
        internal static int LastPlacementCount { get; private set; }

        public void Dispose()
        {
            if (!_map) return;
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(_map);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(_map);
            }
            _map = null;
        }

        /// <summary>Builds the map of a tile from the sources. The terrains are the terrains of the world.</summary>
        internal Texture2D Build(in StampTile tile, PlacementFieldSources sources, IReadOnlyList<UnityEngine.Terrain> terrains)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            int size = Mathf.Clamp(Mathf.RoundToInt(tile.Size.x / TargetCell), 16, 2048);
            float cell = tile.Size.x / size;
            Rect tileRect = tile.WorldRect;
            Rect nearRect = Rect.MinMaxRect(tileRect.xMin - UpdateMargin, tileRect.yMin - UpdateMargin,
                tileRect.xMax + UpdateMargin, tileRect.yMax + UpdateMargin);
            _near.Clear();
            foreach (UnityEngine.Terrain terrain in terrains)
            {
                if (!terrain || !terrain.terrainData) continue;
                Vector3 position = terrain.GetPosition();
                Vector3 terrainSize = terrain.terrainData.size;
                if (nearRect.Overlaps(new Rect(position.x, position.z, terrainSize.x, terrainSize.z)))
                {
                    _near.Add(terrain);
                }
            }

            var discs = new NativeList<float4>(1024, Allocator.TempJob);
            var values = new NativeArray<float2>(size * size, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var output = new NativeArray<half2>(size * size, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                int count = 0;
                foreach (LoogaObjectStamp stamp in sources.Stamps)
                {
                    if (!stamp || !stamp.IsFieldSource) continue;
                    if (stamp.GivesCanopy)
                    {
                        count += AddDiscs(stamp, sources, tileRect, cell, true, discs);
                    }
                    if (stamp.GivesFootprint)
                    {
                        count += AddDiscs(stamp, sources, tileRect, cell, false, discs);
                    }
                }
                new RasterJob
                {
                    Discs = discs.AsArray(),
                    Values = values,
                    Output = output,
                    Size = size,
                    Cell = cell
                }.Run();
                if (!_map || _map.width != size)
                {
                    Dispose();
                    _map = new Texture2D(size, size, TextureFormat.RGHalf, false, true)
                    {
                        name = "Looga Placement Fields",
                        hideFlags = HideFlags.HideAndDontSave,
                        filterMode = FilterMode.Bilinear,
                        wrapMode = TextureWrapMode.Clamp
                    };
                }
                _map.SetPixelData(output, 0);
                _map.Apply(false, false);
                LastPlacementCount = count;
            }
            finally
            {
                discs.Dispose();
                values.Dispose();
                output.Dispose();
            }
            LastBuildMilliseconds = timer.Elapsed.TotalMilliseconds;
            return _map;
        }

        // Adds the crown or footprint discs of the placements of a source near the tile. Canopy discs reach CanopyOuter x
        // radius, footprint discs the foot reach past the radius.
        private int AddDiscs(LoogaObjectStamp stamp, PlacementFieldSources sources, Rect tileRect, float cell, bool canopy,
            NativeList<float4> discs)
        {
            int count = 0;
            foreach (UnityEngine.Terrain terrain in _near)
            {
                List<StampPlacements.Placement>[] lists = sources.Get(stamp, terrain);
                if (lists == null) continue;
                for (int p = 0; p < lists.Length && p < stamp.Prototypes.Count; p++)
                {
                    GameObject prefab = stamp.Prototypes[p].Prefab;
                    if (!prefab || lists[p].Count == 0) continue;
                    Vector2 radii = MeasureRadii(prefab);
                    float baseRadius = (canopy ? radii.x : radii.y) * stamp.FieldRadiusScale;
                    if (baseRadius <= 0f) continue;
                    foreach (StampPlacements.Placement placement in lists[p])
                    {
                        float radius = baseRadius * Mathf.Max(Mathf.Abs(placement.Scale.x), Mathf.Abs(placement.Scale.z));
                        float reach = canopy ? radius * CanopyOuter : radius + FootReach;
                        Vector3 position = placement.Position;
                        if (position.x + reach < tileRect.xMin || position.x - reach > tileRect.xMax ||
                            position.z + reach < tileRect.yMin || position.z - reach > tileRect.yMax) continue;
                        // Texel space: texel i has its centre at i.
                        discs.Add(new float4((position.x - tileRect.xMin) / cell - 0.5f,
                            (position.z - tileRect.yMin) / cell - 0.5f, radius / cell, canopy ? 0f : 1f));
                        count++;
                    }
                }
            }
            return count;
        }

        // Crown radius = largest horizontal half size of the LOD 0 mesh bounds. Footprint radius = largest horizontal
        // distance from the pivot of the vertices in the lowest quarter of the mesh height, at most FootprintHeight
        // above the bottom, so the low branches of a tree do not count as its base. A player cannot read the
        // vertices of a mesh without Read/Write, so the footprint is then half the crown radius.
        private Vector2 MeasureRadii(GameObject prefab)
        {
            if (_radii.TryGetValue(prefab, out Vector2 radii)) return radii;
            var lodGroup = prefab.GetComponentInChildren<LODGroup>();
            Renderer[] renderers = lodGroup && lodGroup.lodCount > 0
                ? lodGroup.GetLODs()[0].renderers
                : prefab.GetComponentsInChildren<Renderer>();
            Matrix4x4 toPrefab = prefab.transform.worldToLocalMatrix;
            var bounds = new Bounds();
            bool hasBounds = false;
            var points = new List<Vector3>();
            bool keep = true;
            foreach (Renderer renderer in renderers)
            {
                if (!renderer || !renderer.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh) continue;
                Mesh mesh = filter.sharedMesh;
                Matrix4x4 toLocal = toPrefab * renderer.transform.localToWorldMatrix;
                Bounds local = mesh.bounds;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 point = toLocal.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents,
                        new Vector3((corner & 1) != 0 ? 1f : -1f, (corner & 2) != 0 ? 1f : -1f, (corner & 4) != 0 ? 1f : -1f)));
                    if (hasBounds)
                    {
                        bounds.Encapsulate(point);
                    }
                    else
                    {
                        bounds = new Bounds(point, Vector3.zero);
                        hasBounds = true;
                    }
                }
                if (!TryReadVertices(mesh, out Vector3[] vertices))
                {
                    // A refused read in the editor is not kept, so the next build measures again.
                    keep &= !Application.isEditor;
                    continue;
                }
                foreach (Vector3 vertex in vertices)
                {
                    points.Add(toLocal.MultiplyPoint3x4(vertex));
                }
            }
            float crown = hasBounds ? Mathf.Max(bounds.extents.x, bounds.extents.z) : 0f;
            float footprint = 0.5f * crown;
            if (points.Count > 0)
            {
                float bottom = float.MaxValue, top = float.MinValue;
                foreach (Vector3 point in points)
                {
                    bottom = Mathf.Min(bottom, point.y);
                    top = Mathf.Max(top, point.y);
                }
                float limit = bottom + Mathf.Min(FootprintBand * (top - bottom), FootprintHeight);
                footprint = 0f;
                foreach (Vector3 point in points)
                {
                    if (point.y <= limit) footprint = Mathf.Max(footprint, new Vector2(point.x, point.z).magnitude);
                }
            }
            radii = new Vector2(crown, footprint);
            if (keep)
            {
                _radii.Add(prefab, radii);
            }
            return radii;
        }

        /// <summary>
        /// Reads the vertices of a mesh. False in a player for a mesh without Read/Write. False also when the editor
        /// refuses the read: after an editor start, Unity refused it for some prefab meshes without Read/Write for a
        /// short time (Kubera World, 2026-11-16), and a later read of the same mesh worked. A caller must then not keep
        /// a measure of the mesh; the stamp world updates the area again (<see cref="TakeRefusedRead"/>). Main thread.
        /// </summary>
        internal static bool TryReadVertices(Mesh mesh, out Vector3[] vertices)
        {
            vertices = null;
            if (!mesh.isReadable && !Application.isEditor) return false;
            vertices = mesh.vertices;
            if (vertices.Length == mesh.vertexCount) return true;
            _refusedRead = true;
            return false;
        }

        /// <summary>True when the editor refused a vertex read since the last call.</summary>
        internal static bool TakeRefusedRead()
        {
            bool refused = _refusedRead;
            _refusedRead = false;
            return refused;
        }

        // Writes the discs into the map: canopy = largest crown cover, foot distance = smallest distance past a footprint.
        [BurstCompile(CompileSynchronously = true)]
        private struct RasterJob : IJob
        {
            // x, y = centre in texels, z = radius in texels, w = 0 for a crown or 1 for a footprint.
            [ReadOnly] public NativeArray<float4> Discs;
            public NativeArray<float2> Values;
            public NativeArray<half2> Output;
            public int Size;
            public float Cell;

            public void Execute()
            {
                for (int i = 0; i < Values.Length; i++)
                {
                    Values[i] = new float2(0f, FootReach);
                }
                for (int d = 0; d < Discs.Length; d++)
                {
                    float4 disc = Discs[d];
                    bool canopy = disc.w < 0.5f;
                    float radius = disc.z;
                    float reach = canopy ? radius * CanopyOuter : radius + FootReach / Cell;
                    int xMin = math.max(0, (int)math.floor(disc.x - reach));
                    int xMax = math.min(Size - 1, (int)math.ceil(disc.x + reach));
                    int zMin = math.max(0, (int)math.floor(disc.y - reach));
                    int zMax = math.min(Size - 1, (int)math.ceil(disc.y + reach));
                    float inner = radius * CanopyInner;
                    float outer = radius * CanopyOuter;
                    for (int z = zMin; z <= zMax; z++)
                    {
                        float dz = z - disc.y;
                        int row = z * Size;
                        for (int x = xMin; x <= xMax; x++)
                        {
                            float dx = x - disc.x;
                            float distance = math.sqrt(dx * dx + dz * dz);
                            if (distance > reach) continue;
                            float2 value = Values[row + x];
                            if (canopy)
                            {
                                value.x = math.max(value.x, math.smoothstep(outer, inner, distance));
                            }
                            else
                            {
                                value.y = math.min(value.y, math.max(0f, distance - radius) * Cell);
                            }
                            Values[row + x] = value;
                        }
                    }
                }
                for (int i = 0; i < Values.Length; i++)
                {
                    Output[i] = new half2(Values[i]);
                }
            }
        }
    }
}
