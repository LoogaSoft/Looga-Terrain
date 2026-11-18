using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Builds water fields for one tile on the GPU and keeps them in a cache with a memory budget. Field x = signed
    /// distance in metres to the nearest water edge (negative inside). Field y = water level in world metres at the
    /// nearest point of the nearest edge. Without an edge in reach, y is the highest surface inside water and
    /// -NotFound outside, so "height above water" = ground height - y.
    /// The field covers the tile like the spline distance fields: tile UV 0 to 1 over the heightmap sample span.
    /// The caller must invalidate an area after its sources or settings change.
    /// </summary>
    /// <remarks>
    /// A raster pass draws the source meshes top-down: inside mask and surface height. The CPU collects the mesh
    /// boundary edges and sorts them into bins. A compute pass then takes the exact distance to the edges of each bin.
    /// A boundary edge with another source just outside it is not a water edge: two rivers that meet end to end, or a
    /// river mouth inside a lake, have no shore between them.
    /// </remarks>
    internal sealed class WaterDistanceField : IDisposable
    {
        /// <summary>Distance and surface value where no water edge is in reach.</summary>
        internal const float NotFound = 99999f;
        private const string SurfaceShaderName = "LoogaWaterSurface";
        private const string ComputeName = "LoogaWaterDistance";
        private const int BinCount = 32;
        // Vertices closer than this weld to one vertex, in metres.
        private const float WeldDistance = 0.001f;
        // An edge is covered when another source contains the point this far outside the edge middle, in metres.
        private const float CoverProbe = 0.05f;
        // Smallest cell size of the triangle grid for the cover test, in metres.
        private const float CoverCell = 8f;
        // Most cells along one side of the grid. A very large surface gets larger cells, because each triangle goes
        // into all cells of its bounds.
        private const float MaxCoverCells = 1024f;

        private static readonly int FieldRectId = Shader.PropertyToID("_FieldRect");
        private static readonly int SurfaceId = Shader.PropertyToID("_Surface");
        private static readonly int FieldId = Shader.PropertyToID("_Field");
        private static readonly int EdgesId = Shader.PropertyToID("_Edges");
        private static readonly int EdgeHeightsId = Shader.PropertyToID("_EdgeHeights");
        private static readonly int BinEdgesId = Shader.PropertyToID("_BinEdges");
        private static readonly int BinsId = Shader.PropertyToID("_Bins");
        private static readonly int ResolutionId = Shader.PropertyToID("_Resolution");
        private static readonly int BinCountId = Shader.PropertyToID("_BinCount");
        private static readonly int MaxDistanceId = Shader.PropertyToID("_MaxDistance");
        private static readonly int NotFoundId = Shader.PropertyToID("_NotFound");

        // Boundary edges of one source surface, kept until the surface changes. The triangle grid is built when another
        // source first tests a point against this surface.
        private sealed class SourceEdges
        {
            internal int Mesh;
            internal int VertexCount;
            internal int Revision;
            internal Matrix4x4 Transform;
            internal readonly List<Vector4> Edges = new();
            internal readonly List<Vector2> Heights = new();
            internal readonly List<Vector2> Probes = new();
            internal readonly List<Vector2> Triangles = new();
            internal Dictionary<long, List<int>> Cells;
            internal float CellSize = CoverCell;
        }

        // A source of the field that is being built, with its world XZ rectangle.
        private readonly struct TileSource
        {
            internal readonly SourceEdges Edges;
            internal readonly Mesh Mesh;
            internal readonly Matrix4x4 LocalToWorld;
            internal readonly Rect Bounds;

            internal TileSource(SourceEdges edges, Mesh mesh, Matrix4x4 localToWorld, Rect bounds)
            {
                Edges = edges;
                Mesh = mesh;
                LocalToWorld = localToWorld;
                Bounds = bounds;
            }
        }

        private readonly TileFieldCache _cache = new(256L * 1024 * 1024);
        private readonly Material _surfaceMaterial;
        private readonly ComputeShader _compute;
        private readonly int _kernel;
        private readonly CommandBuffer _commands = new() { name = "Looga Water Surface" };
        private readonly Dictionary<LoogaWaterSource, SourceEdges> _sourceEdges = new();
        private readonly List<TileSource> _tileSources = new();
        private readonly List<Vector4> _tileEdges = new();
        private readonly List<Vector2> _tileEdgeHeights = new();
        private readonly int[] _binCounts = new int[BinCount * BinCount];
        private readonly Vector2Int[] _bins = new Vector2Int[BinCount * BinCount];
        private readonly List<int> _binEdges = new();
        private RenderTexture _surface;
        private Texture2D _emptyField;
        private GraphicsBuffer _edgeBuffer;
        private GraphicsBuffer _edgeHeightBuffer;
        private GraphicsBuffer _binEdgeBuffer;
        private GraphicsBuffer _binBuffer;

        /// <summary>Cache memory limit in bytes.</summary>
        internal long CacheBudget
        {
            get => _cache.Budget;
            set => _cache.Budget = value;
        }

        internal long CachedBytes => _cache.CachedBytes;
        internal int BuildCount => _cache.BuildCount;
        internal int CacheHits => _cache.CacheHits;

        internal WaterDistanceField()
        {
            var shader = Resources.Load<Shader>(SurfaceShaderName);
            _compute = Resources.Load<ComputeShader>(ComputeName);
            if (!shader || !_compute)
            {
                throw new NotSupportedException("Looga water fields require the " + SurfaceShaderName + " shader and the " +
                    ComputeName + " compute shader.");
            }
            _surfaceMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _kernel = _compute.FindKernel("WaterDistance");
        }

        public void Dispose()
        {
            _cache.Dispose();
            _commands.Release();
            _edgeBuffer?.Dispose();
            _edgeHeightBuffer?.Dispose();
            _binEdgeBuffer?.Dispose();
            _binBuffer?.Dispose();
            if (_surface)
            {
                _surface.Release();
                UnityEngine.Object.DestroyImmediate(_surface);
            }
            UnityEngine.Object.DestroyImmediate(_surfaceMaterial);
            if (_emptyField)
            {
                UnityEngine.Object.DestroyImmediate(_emptyField);
            }
        }

        internal void ResetStatistics()
        {
            _cache.ResetStatistics();
        }

        /// <summary>
        /// Cached water field of an area for one tile. A tile with no source in reach gets one shared 1 x 1 field
        /// (no water found), so a filter that also reads far from water does not fill the cache.
        /// </summary>
        internal Texture Get(in StampTile tile, LoogaWaterArea area)
        {
            if (_cache.TryGet(area, tile, out RenderTexture cached)) return cached;
            if (!AnySourceInReach(tile, area)) return GetEmptyField();
            using (StampSections.Measure(StampSection.FieldBuild))
            {
                RenderTexture field = Build(tile, area);
                _cache.Add(area, tile, field);
                return field;
            }
        }

        /// <summary>Removes the cached fields of one area on the tiles that a world rectangle touches.</summary>
        internal void Invalidate(LoogaWaterArea area, Rect? region = null)
        {
            _cache.Invalidate(area, region);
        }

        internal void ClearCache()
        {
            _cache.Clear();
            _sourceEdges.Clear();
        }

        #region Geometry
        /// <summary>World XZ rectangle of a surface mesh.</summary>
        internal static Rect SurfaceRect(Mesh mesh, Matrix4x4 localToWorld)
        {
            Bounds bounds = mesh.bounds;
            float xMin = float.MaxValue, zMin = float.MaxValue, xMax = float.MinValue, zMax = float.MinValue;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 local = bounds.min + Vector3.Scale(bounds.size,
                    new Vector3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1));
                Vector3 world = localToWorld.MultiplyPoint3x4(local);
                xMin = Mathf.Min(xMin, world.x);
                zMin = Mathf.Min(zMin, world.z);
                xMax = Mathf.Max(xMax, world.x);
                zMax = Mathf.Max(zMax, world.z);
            }
            return Rect.MinMaxRect(xMin, zMin, xMax, zMax);
        }

        /// <summary>
        /// Adds the boundary edges of a surface mesh in world XZ (x, z of the first point, x, z of the second point). A
        /// boundary edge belongs to one triangle only. Vertices at the same position count as one vertex, so UV seams
        /// do not give edges. A mesh that is not readable gives no edges.
        /// </summary>
        /// <param name="heights">Optional: receives the world heights of the two points of each edge.</param>
        /// <param name="probes">Optional: receives a world XZ point CoverProbe outside the middle of each edge.</param>
        internal static void GetBoundaryEdges(Mesh mesh, Matrix4x4 localToWorld, List<Vector4> edges,
            List<Vector2> heights = null, List<Vector2> probes = null)
        {
            if (!mesh || !mesh.isReadable) return;
            Vector3[] vertices = mesh.vertices;
            var welded = new int[vertices.Length];
            var positions = new Dictionary<Vector3Int, int>(vertices.Length, WeldComparer.Instance);
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 world = localToWorld.MultiplyPoint3x4(vertices[i]);
                vertices[i] = world;
                var key = Vector3Int.RoundToInt(world / WeldDistance);
                if (!positions.TryGetValue(key, out welded[i]))
                {
                    welded[i] = i;
                    positions[key] = i;
                }
            }
            // Sorted edge keys put the uses of one edge next to each other, without hashing. Each key keeps the third
            // corner of its triangle, which gives the inner side of a boundary edge.
            var keys = new List<(long Key, int Third)>();
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
                int[] triangles = mesh.GetTriangles(submesh);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int a = welded[triangles[i + corner]];
                        int b = welded[triangles[i + (corner + 1) % 3]];
                        if (a == b) continue;
                        keys.Add(((long)Mathf.Min(a, b) << 32 | (uint)Mathf.Max(a, b),
                            welded[triangles[i + (corner + 2) % 3]]));
                    }
                }
            }
            keys.Sort((first, second) => first.Key.CompareTo(second.Key));
            for (int start = 0; start < keys.Count;)
            {
                int end = start + 1;
                while (end < keys.Count && keys[end].Key == keys[start].Key) end++;
                if (end - start == 1)
                {
                    Vector3 a = vertices[(int)(keys[start].Key >> 32)];
                    Vector3 b = vertices[(int)(keys[start].Key & 0xFFFFFFFF)];
                    edges.Add(new Vector4(a.x, a.z, b.x, b.z));
                    heights?.Add(new Vector2(a.y, b.y));
                    probes?.Add(OutsideProbe(a, b, vertices[keys[start].Third]));
                }
                start = end;
            }
        }

        // A point CoverProbe from the middle of edge a-b, on the side away from the third triangle corner.
        private static Vector2 OutsideProbe(Vector3 a, Vector3 b, Vector3 third)
        {
            var start = new Vector2(a.x, a.z);
            Vector2 along = new Vector2(b.x, b.z) - start;
            var normal = new Vector2(-along.y, along.x).normalized;
            if (Vector2.Dot(normal, new Vector2(third.x, third.z) - start) > 0f) normal = -normal;
            return start + along * 0.5f + normal * CoverProbe;
        }

        // Adds the triangles of a surface mesh in world XZ, three points for each triangle, and sorts them into cells.
        private static void BuildCells(SourceEdges source, Mesh mesh, Matrix4x4 localToWorld)
        {
            source.Triangles.Clear();
            source.Cells = new Dictionary<long, List<int>>();
            if (!mesh.isReadable) return;
            Bounds bounds = mesh.bounds;
            float cellSize = Mathf.Max(CoverCell, Mathf.Max(bounds.size.x, bounds.size.z) / MaxCoverCells);
            source.CellSize = cellSize;
            Vector3[] vertices = mesh.vertices;
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
                int[] triangles = mesh.GetTriangles(submesh);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int index = source.Triangles.Count / 3;
                    float xMin = float.MaxValue, zMin = float.MaxValue, xMax = float.MinValue, zMax = float.MinValue;
                    for (int corner = 0; corner < 3; corner++)
                    {
                        Vector3 world = localToWorld.MultiplyPoint3x4(vertices[triangles[i + corner]]);
                        source.Triangles.Add(new Vector2(world.x, world.z));
                        xMin = Mathf.Min(xMin, world.x);
                        zMin = Mathf.Min(zMin, world.z);
                        xMax = Mathf.Max(xMax, world.x);
                        zMax = Mathf.Max(zMax, world.z);
                    }
                    for (int z = Mathf.FloorToInt(zMin / cellSize); z <= Mathf.FloorToInt(zMax / cellSize); z++)
                    {
                        for (int x = Mathf.FloorToInt(xMin / cellSize); x <= Mathf.FloorToInt(xMax / cellSize); x++)
                        {
                            long key = (long)x << 32 | (uint)z;
                            if (!source.Cells.TryGetValue(key, out List<int> cell))
                            {
                                cell = new List<int>();
                                source.Cells[key] = cell;
                            }
                            cell.Add(index);
                        }
                    }
                }
            }
        }

        // True when a triangle of the surface contains the world XZ point. Points on a triangle edge count as inside.
        private static bool Covers(SourceEdges source, Vector2 point)
        {
            float cellSize = source.CellSize;
            long key = (long)Mathf.FloorToInt(point.x / cellSize) << 32 | (uint)Mathf.FloorToInt(point.y / cellSize);
            if (!source.Cells.TryGetValue(key, out List<int> cell)) return false;
            foreach (int index in cell)
            {
                Vector2 a = source.Triangles[index * 3];
                Vector2 b = source.Triangles[index * 3 + 1];
                Vector2 c = source.Triangles[index * 3 + 2];
                float d1 = Cross(b - a, point - a);
                float d2 = Cross(c - b, point - b);
                float d3 = Cross(a - c, point - c);
                bool negative = d1 < -1e-6f || d2 < -1e-6f || d3 < -1e-6f;
                bool positive = d1 > 1e-6f || d2 > 1e-6f || d3 > 1e-6f;
                if (!(negative && positive)) return true;
            }
            return false;
        }

        private static float Cross(Vector2 first, Vector2 second)
        {
            return first.x * second.y - first.y * second.x;
        }

        // True when another source of the field contains the outside probe of the edge.
        private bool IsCovered(int sourceIndex, int edgeIndex)
        {
            Vector2 probe = _tileSources[sourceIndex].Edges.Probes[edgeIndex];
            for (int other = 0; other < _tileSources.Count; other++)
            {
                if (other == sourceIndex) continue;
                TileSource source = _tileSources[other];
                if (!source.Bounds.Contains(probe)) continue;
                if (source.Edges.Cells == null) BuildCells(source.Edges, source.Mesh, source.LocalToWorld);
                if (Covers(source.Edges, probe)) return true;
            }
            return false;
        }

        // Vector3Int.GetHashCode mixes the axes weakly, so millimetre keys on a grid collide often.
        private sealed class WeldComparer : IEqualityComparer<Vector3Int>
        {
            internal static readonly WeldComparer Instance = new();

            public bool Equals(Vector3Int a, Vector3Int b) => a == b;

            public int GetHashCode(Vector3Int key) =>
                unchecked(key.x * 73856093 ^ key.y * 19349663 ^ key.z * 83492791);
        }

        private SourceEdges GetSourceEdges(LoogaWaterSource source, Mesh mesh, Matrix4x4 localToWorld)
        {
            if (!_sourceEdges.TryGetValue(source, out SourceEdges cached))
            {
                cached = new SourceEdges { Mesh = 0 };
                _sourceEdges[source] = cached;
            }
            if (cached.Mesh != mesh.GetInstanceID() || cached.VertexCount != mesh.vertexCount ||
                cached.Revision != source.Revision || cached.Transform != localToWorld)
            {
                cached.Mesh = mesh.GetInstanceID();
                cached.VertexCount = mesh.vertexCount;
                cached.Revision = source.Revision;
                cached.Transform = localToWorld;
                cached.Edges.Clear();
                cached.Heights.Clear();
                cached.Probes.Clear();
                cached.Triangles.Clear();
                cached.Cells = null;
                if (!mesh.isReadable)
                {
                    Debug.LogWarning("Looga water source " + source.name + ": the mesh " + mesh.name +
                        " is not readable, so it has no water edge.", source);
                }
                GetBoundaryEdges(mesh, localToWorld, cached.Edges, cached.Heights, cached.Probes);
            }
            return cached;
        }
        #endregion

        #region Build
        private static Rect FieldRect(in StampTile tile, float reach)
        {
            return Rect.MinMaxRect(tile.Position.x - reach, tile.Position.z - reach,
                tile.Position.x + tile.SampleSpan.x + reach, tile.Position.z + tile.SampleSpan.y + reach);
        }

        private static bool AnySourceInReach(in StampTile tile, LoogaWaterArea area)
        {
            Rect search = FieldRect(tile, area.MaxDistance);
            foreach (LoogaWaterSource source in area.Sources)
            {
                if (!source || !source.isActiveAndEnabled) continue;
                if (!source.TryGetSurface(out Mesh mesh, out Matrix4x4 localToWorld)) continue;
                if (StampMapping.Touches(SurfaceRect(mesh, localToWorld), search)) return true;
            }
            return false;
        }

        // Distance "not found" (outside water) and level "not found" everywhere.
        private Texture GetEmptyField()
        {
            if (_emptyField) return _emptyField;
            _emptyField = new Texture2D(1, 1, GraphicsFormat.R32G32_SFloat, TextureCreationFlags.None)
            {
                name = "Looga Stamps Empty Water Field",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.HideAndDontSave
            };
            _emptyField.SetPixelData(new[] { NotFound, -NotFound }, 0);
            _emptyField.Apply(false, true);
            return _emptyField;
        }

        private RenderTexture Build(in StampTile tile, LoogaWaterArea area)
        {
            int resolution = area.Resolution;
            float reach = area.MaxDistance;
            var fieldRect = new Rect(tile.Position.x, tile.Position.z, tile.SampleSpan.x, tile.SampleSpan.y);
            Rect search = Rect.MinMaxRect(fieldRect.xMin - reach, fieldRect.yMin - reach, fieldRect.xMax + reach,
                fieldRect.yMax + reach);
            EnsureSurface(resolution);

            _commands.Clear();
            _commands.SetRenderTarget(_surface);
            _commands.ClearRenderTarget(false, true, new Color(0f, -NotFound, 0f, 0f));
            _tileSources.Clear();
            _tileEdges.Clear();
            _tileEdgeHeights.Clear();
            var fieldRectVector = new Vector4(fieldRect.x, fieldRect.y, 1f / fieldRect.width, 1f / fieldRect.height);
            _surfaceMaterial.SetVector(FieldRectId, fieldRectVector);
            foreach (LoogaWaterSource source in area.Sources)
            {
                if (!source || !source.isActiveAndEnabled) continue;
                if (!source.TryGetSurface(out Mesh mesh, out Matrix4x4 localToWorld)) continue;
                Rect bounds = SurfaceRect(mesh, localToWorld);
                if (!StampMapping.Touches(bounds, search)) continue;
                if (StampMapping.Touches(bounds, fieldRect))
                {
                    for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                    {
                        _commands.DrawMesh(mesh, localToWorld, _surfaceMaterial, submesh, 0);
                    }
                }
                _tileSources.Add(new TileSource(GetSourceEdges(source, mesh, localToWorld), mesh, localToWorld, bounds));
            }
            // The edges after all sources, so each edge can test the other surfaces.
            for (int sourceIndex = 0; sourceIndex < _tileSources.Count; sourceIndex++)
            {
                SourceEdges sourceEdges = _tileSources[sourceIndex].Edges;
                for (int i = 0; i < sourceEdges.Edges.Count; i++)
                {
                    Vector4 edge = sourceEdges.Edges[i];
                    if (Mathf.Max(edge.x, edge.z) < search.xMin || Mathf.Min(edge.x, edge.z) > search.xMax ||
                        Mathf.Max(edge.y, edge.w) < search.yMin || Mathf.Min(edge.y, edge.w) > search.yMax) continue;
                    if (IsCovered(sourceIndex, i)) continue;
                    _tileEdges.Add(edge);
                    _tileEdgeHeights.Add(sourceEdges.Heights[i]);
                }
            }
            _tileSources.Clear();
            RenderTexture previous = RenderTexture.active;
            Graphics.ExecuteCommandBuffer(_commands);
            RenderTexture.active = previous;
            _commands.Clear();

            FillBins(fieldRect, reach);
            var field = new RenderTexture(new RenderTextureDescriptor(resolution, resolution,
                GraphicsFormat.R32G32_SFloat, 0) { enableRandomWrite = true })
            {
                name = "Looga Stamps Water Field " + area.name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            field.Create();
            _edgeBuffer = EnsureBuffer(_edgeBuffer, _tileEdges.Count, 16);
            _edgeHeightBuffer = EnsureBuffer(_edgeHeightBuffer, _tileEdges.Count, 8);
            _binEdgeBuffer = EnsureBuffer(_binEdgeBuffer, _binEdges.Count, 4);
            _binBuffer = EnsureBuffer(_binBuffer, _bins.Length, 8);
            if (_tileEdges.Count > 0)
            {
                _edgeBuffer.SetData(_tileEdges);
                _edgeHeightBuffer.SetData(_tileEdgeHeights);
            }
            if (_binEdges.Count > 0) _binEdgeBuffer.SetData(_binEdges);
            _binBuffer.SetData(_bins);
            _compute.SetTexture(_kernel, SurfaceId, _surface);
            _compute.SetTexture(_kernel, FieldId, field);
            _compute.SetBuffer(_kernel, EdgesId, _edgeBuffer);
            _compute.SetBuffer(_kernel, EdgeHeightsId, _edgeHeightBuffer);
            _compute.SetBuffer(_kernel, BinEdgesId, _binEdgeBuffer);
            _compute.SetBuffer(_kernel, BinsId, _binBuffer);
            _compute.SetInt(ResolutionId, resolution);
            _compute.SetInt(BinCountId, BinCount);
            _compute.SetVector(FieldRectId, new Vector4(fieldRect.x, fieldRect.y, fieldRect.width, fieldRect.height));
            _compute.SetFloat(MaxDistanceId, reach);
            _compute.SetFloat(NotFoundId, NotFound);
            int groups = Mathf.CeilToInt(resolution / 8f);
            _compute.Dispatch(_kernel, groups, groups, 1);
            return field;
        }

        // Puts each edge into every bin whose rectangle, widened by the reach, touches the edge bounds. A sample then
        // finds every edge in reach in its own bin.
        private void FillBins(Rect fieldRect, float reach)
        {
            Array.Clear(_binCounts, 0, _binCounts.Length);
            _binEdges.Clear();
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    int start = 0;
                    for (int i = 0; i < _bins.Length; i++)
                    {
                        _bins[i] = new Vector2Int(start, 0);
                        start += _binCounts[i];
                    }
                    for (int i = 0; i < start; i++)
                    {
                        _binEdges.Add(0);
                    }
                }
                for (int index = 0; index < _tileEdges.Count; index++)
                {
                    Vector4 edge = _tileEdges[index];
                    int x0 = BinOf(Mathf.Min(edge.x, edge.z) - reach, fieldRect.xMin, fieldRect.width);
                    int x1 = BinOf(Mathf.Max(edge.x, edge.z) + reach, fieldRect.xMin, fieldRect.width);
                    int z0 = BinOf(Mathf.Min(edge.y, edge.w) - reach, fieldRect.yMin, fieldRect.height);
                    int z1 = BinOf(Mathf.Max(edge.y, edge.w) + reach, fieldRect.yMin, fieldRect.height);
                    for (int z = z0; z <= z1; z++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            int bin = z * BinCount + x;
                            if (pass == 0)
                            {
                                _binCounts[bin]++;
                                continue;
                            }
                            Vector2Int range = _bins[bin];
                            _binEdges[range.x + range.y] = index;
                            _bins[bin] = new Vector2Int(range.x, range.y + 1);
                        }
                    }
                }
            }
        }

        private static GraphicsBuffer EnsureBuffer(GraphicsBuffer buffer, int count, int stride)
        {
            count = Mathf.Max(1, count);
            if (buffer != null && buffer.count >= count) return buffer;
            buffer?.Dispose();
            return new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(count), stride);
        }

        private static int BinOf(float position, float start, float size)
        {
            return Mathf.Clamp(Mathf.FloorToInt((position - start) / size * BinCount), 0, BinCount - 1);
        }

        private void EnsureSurface(int resolution)
        {
            if (_surface && _surface.width == resolution) return;
            if (_surface)
            {
                _surface.Release();
                UnityEngine.Object.DestroyImmediate(_surface);
            }
            _surface = new RenderTexture(new RenderTextureDescriptor(resolution, resolution,
                GraphicsFormat.R32G32_SFloat, 0))
            {
                name = "Looga Stamps Water Surface",
                filterMode = FilterMode.Point
            };
            _surface.Create();
        }
        #endregion
    }
}
