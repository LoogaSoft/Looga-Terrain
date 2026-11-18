using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Geology
{
    // Cuts rock face strips into chunks along the cliff. Each chunk has one mesh for each level of detail. A level
    // uses every 2^level column and row of the same grid, so chunks and levels meet without gaps at their seams.
    // Submesh 0 is the rock. A mesh with terrain cover has submesh 1: the covered quads again, for the terrain surface
    // mesh material.
    internal static class RockFaceChunks
    {
        public const int LevelCount = 3;
        private const int MaximumChunkVertices = 32000;

        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex
        {
            public Vector3 Position;
            public half4 Normal;
            public Color32 Color;
            public half2 GroundOffset;
            public half4 Cover;
        }

        private static readonly VertexAttributeDescriptor[] Layout =
        {
            new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new(VertexAttribute.Normal, VertexAttributeFormat.Float16, 4),
            new(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new(VertexAttribute.TexCoord1, VertexAttributeFormat.Float16, 2),
            new(VertexAttribute.TexCoord2, VertexAttributeFormat.Float16, 4)
        };

        // Buffers of the mesh in work, used again by the next mesh so a rebuild makes no new arrays. The meshes are made
        // on the main thread only. The buffers grow to the largest chunk (MaximumChunkVertices), about 2 MB.
        private static readonly List<int> ColumnLines = new();
        private static readonly List<int> RowLines = new();
        private static Vertex[] _vertices = new Vertex[0];
        private static int[] _indices = new int[0];
        private static int[] _coverIndices = new int[0];
        private static ushort[] _shortIndices = new ushort[0];
        private static int[] _remap = new int[0];

        public struct Chunk
        {
            public Mesh[] Levels;
            public Bounds Bounds;
            // Group of the chunk columns, for example the terrain tile below them.
            public int Group;
        }

        /// <param name="chunkColumns">Largest number of quad columns in one chunk.</param>
        /// <param name="columnGroups">
        /// Group of each grid column. A chunk ends where the group changes, so each chunk has one group.
        /// </param>
        /// <param name="readableLevel">Level whose mesh keeps its CPU copy, for example for a collider. -1 = none.</param>
        /// <param name="detailAreas">
        /// World areas where the level 0 mesh also keeps its CPU copy, for example for a cave entrance that copies the
        /// wall shape. Null = none.
        /// </param>
        public static List<Chunk> Create(RockFaceStrip strip, int chunkColumns, int[] columnGroups, string name,
            int readableLevel = -1, IReadOnlyList<Bounds> detailAreas = null)
        {
            List<Chunk> chunks = new();
            int quadColumns = strip.Loop ? strip.Columns : strip.Columns - 1;
            int columnsPerChunk = Mathf.Clamp(Mathf.Min(chunkColumns, MaximumChunkVertices / strip.Rows - 1), 4,
                Mathf.Max(4, quadColumns));
            int last;
            for (int first = 0; first < quadColumns; first = last)
            {
                int group = columnGroups[first];
                last = first + 1;
                while (last < quadColumns && last - first < columnsPerChunk && columnGroups[last] == group)
                {
                    last++;
                }
                Chunk chunk = new() { Levels = new Mesh[LevelCount], Group = group };
                for (int level = 0; level < LevelCount; level++)
                {
                    chunk.Levels[level] = CreateMesh(strip, first, last, 1 << level, name + " LOD" + level,
                        level == readableLevel, level == 0 ? detailAreas : null);
                }
                // A chunk that has only hidden quads has no triangles at any level.
                if (IsEmpty(chunk)) continue;
                chunk.Bounds = chunk.Levels[0].bounds;
                chunks.Add(chunk);
            }
            return chunks;
        }

        // Grid lines first, first + step, ..., last. The last line is always included, so the chunk edges match.
        private static void Lines(int first, int last, int step, List<int> lines)
        {
            lines.Clear();
            for (int line = first; line < last; line += step)
            {
                lines.Add(line);
            }
            lines.Add(last);
        }

        // A buffer of at least the length, used again for the next mesh. Keep copies the old values into a new buffer.
        private static T[] Reserve<T>(ref T[] buffer, int length, bool keep = false)
        {
            if (buffer.Length >= length) return buffer;
            var larger = new T[Mathf.Max(length, buffer.Length * 2)];
            if (keep)
            {
                System.Array.Copy(buffer, larger, buffer.Length);
            }
            buffer = larger;
            return buffer;
        }

        /// <summary>One chunk from whole strips, for example the blocks of a rock formation near each other.</summary>
        /// <param name="readableLevel">Level whose mesh keeps its CPU copy, for example for a collider. -1 = none.</param>
        /// <param name="detailAreas">World areas where the level 0 mesh also keeps its CPU copy. Null = none.</param>
        /// <returns>The chunk, or null when all quads of the strips are hidden.</returns>
        public static Chunk? CreateCombined(IReadOnlyList<RockFaceStrip> strips, int group, string name,
            int readableLevel = -1, IReadOnlyList<Bounds> detailAreas = null)
        {
            Chunk chunk = new() { Levels = new Mesh[LevelCount], Group = group };
            for (int level = 0; level < LevelCount; level++)
            {
                int vertexCount = 0;
                int indexCount = 0;
                int coverCount = 0;
                foreach (RockFaceStrip strip in strips)
                {
                    AppendStrip(strip, 0, strip.Loop ? strip.Columns : strip.Columns - 1, 1 << level, ref vertexCount,
                        ref indexCount, ref coverCount);
                }
                chunk.Levels[level] = FinishMesh(vertexCount, indexCount, coverCount, name + " LOD" + level,
                    level == readableLevel, level == 0 ? detailAreas : null);
            }
            if (IsEmpty(chunk)) return null;
            chunk.Bounds = chunk.Levels[0].bounds;
            return chunk;
        }

        // True when the finest level has no triangles. The meshes of an empty chunk are destroyed. A coarser level
        // drops a quad only when all grid points of its span are hidden, so it is empty too.
        private static bool IsEmpty(Chunk chunk)
        {
            Mesh finest = chunk.Levels[0];
            if (finest.GetIndexCount(0) > 0 || finest.subMeshCount > 1 && finest.GetIndexCount(1) > 0) return false;
            foreach (Mesh mesh in chunk.Levels)
            {
                if (Application.isPlaying) Object.Destroy(mesh);
                else Object.DestroyImmediate(mesh);
            }
            return true;
        }

        private static Mesh CreateMesh(RockFaceStrip strip, int firstColumn, int lastColumn, int step, string name,
            bool readable, IReadOnlyList<Bounds> detailAreas)
        {
            int vertexCount = 0;
            int indexCount = 0;
            int coverCount = 0;
            AppendStrip(strip, firstColumn, lastColumn, step, ref vertexCount, ref indexCount, ref coverCount);
            return FinishMesh(vertexCount, indexCount, coverCount, name, readable, detailAreas);
        }

        // Adds the triangles of a column range of the strip after the ones already in the buffers, and only the
        // vertices that these triangles use. Quads that nobody can see leave no vertices. A quad with terrain cover at
        // a corner also goes into the cover triangles.
        private static void AppendStrip(RockFaceStrip strip, int firstColumn, int lastColumn, int step,
            ref int vertexStart, ref int indexStart, ref int coverStart)
        {
            List<int> columns = ColumnLines;
            List<int> rows = RowLines;
            Lines(firstColumn, lastColumn, step, columns);
            Lines(0, strip.Rows - 1, step, rows);
            int gridCount = columns.Count * rows.Count;

            // Triangles first, with grid indices of this range.
            int quadCount = (columns.Count - 1) * (rows.Count - 1);
            int[] indices = Reserve(ref _indices, indexStart + quadCount * 6, true);
            int[] coverIndices = Reserve(ref _coverIndices, coverStart + quadCount * 6, true);
            int write = indexStart;
            int coverWrite = coverStart;
            for (int column = 0; column < columns.Count - 1; column++)
            {
                for (int row = 0; row < rows.Count - 1; row++)
                {
                    // A quad that nobody can see gets no triangles.
                    if (strip.CullHidden && IsHidden(strip, columns[column], columns[column + 1], rows[row],
                            rows[row + 1]))
                    {
                        continue;
                    }
                    // A top quad with a corner outside the top gets no triangles.
                    if (strip.IsTop && !(strip.Active[strip.Index(columns[column] % strip.Columns, rows[row])] &&
                                         strip.Active[strip.Index(columns[column + 1] % strip.Columns, rows[row])] &&
                                         strip.Active[strip.Index(columns[column] % strip.Columns, rows[row + 1])] &&
                                         strip.Active[strip.Index(columns[column + 1] % strip.Columns, rows[row + 1])]))
                    {
                        continue;
                    }
                    int a = column * rows.Count + row;
                    int b = a + rows.Count;
                    int quadStart = write;
                    if (strip.FlipWinding)
                    {
                        indices[write++] = a;
                        indices[write++] = a + 1;
                        indices[write++] = b;
                        indices[write++] = b;
                        indices[write++] = a + 1;
                        indices[write++] = b + 1;
                    }
                    else
                    {
                        indices[write++] = a;
                        indices[write++] = b;
                        indices[write++] = a + 1;
                        indices[write++] = b;
                        indices[write++] = b + 1;
                        indices[write++] = a + 1;
                    }
                    float cover = Coverage(strip, columns[column], columns[column + 1], rows[row], rows[row + 1]);
                    if (cover > 0f)
                    {
                        System.Array.Copy(indices, quadStart, coverIndices, coverWrite, 6);
                        coverWrite += 6;
                    }
                    // A fully covered quad shows only the terrain surface, and a quad with a show value of 0 at every
                    // grid point has no rock. The rock is not drawn there and casts no shadow: at a low sun, a rock top
                    // so close to the terrain shadowed the land next to it.
                    if (cover >= 0.999f ||
                        !IsShown(strip, columns[column], columns[column + 1], rows[row], rows[row + 1]))
                    {
                        write = quadStart;
                    }
                }
            }

            // Then the used vertices, in the order of first use.
            int[] remap = Reserve(ref _remap, gridCount);
            System.Array.Fill(remap, -1, 0, gridCount);
            Vertex[] vertices = Reserve(ref _vertices, vertexStart + gridCount, true);
            int used = 0;
            for (int index = indexStart; index < write + coverWrite - coverStart; index++)
            {
                // The rock triangles, then the cover triangles.
                int[] buffer = index < write ? indices : coverIndices;
                int position = index < write ? index : coverStart + index - write;
                int grid = buffer[position];
                if (remap[grid] < 0)
                {
                    remap[grid] = used;
                    // A loop wraps its last quad column to column 0.
                    int gridColumn = columns[grid / rows.Count] % strip.Columns;
                    int source = strip.Index(gridColumn, rows[grid % rows.Count]);
                    Vector3 normal = strip.Normals[source];
                    Vector4 cover = strip.TerrainCover[source];
                    vertices[vertexStart + used] = new Vertex
                    {
                        Position = strip.Positions[source],
                        // w holds the show value of the vertex.
                        Normal = new half4((half)normal.x, (half)normal.y, (half)normal.z, (half)strip.Show[source]),
                        Color = strip.Colors[source],
                        GroundOffset = new half2((half)strip.GroundOffsets[source].x,
                            (half)strip.GroundOffsets[source].y),
                        Cover = new half4((half)cover.x, (half)cover.y, (half)cover.z, (half)cover.w)
                    };
                    used++;
                }
                buffer[position] = vertexStart + remap[grid];
            }
            vertexStart += used;
            indexStart = write;
            coverStart = coverWrite;
        }

        // True when a grid point of the quad span has a show value over 0.
        private static bool IsShown(RockFaceStrip strip, int firstColumn, int lastColumn, int firstRow, int lastRow)
        {
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                int gridColumn = column % strip.Columns;
                for (int row = firstRow; row <= lastRow; row++)
                {
                    if (strip.Show[strip.Index(gridColumn, row)] > 0.001f) return true;
                }
            }
            return false;
        }

        // Terrain cover of the quad span: 0 when no grid point has cover, 1 when all grid points have full cover, else
        // between.
        private static float Coverage(RockFaceStrip strip, int firstColumn, int lastColumn, int firstRow, int lastRow)
        {
            float minimum = 1f;
            float maximum = 0f;
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                int gridColumn = column % strip.Columns;
                for (int row = firstRow; row <= lastRow; row++)
                {
                    float cover = strip.TerrainCover[strip.Index(gridColumn, row)].z;
                    minimum = Mathf.Min(minimum, cover);
                    maximum = Mathf.Max(maximum, cover);
                }
            }
            return maximum <= 0f ? 0f : Mathf.Clamp(minimum, 0.001f, 1f);
        }

        // True when every grid point of the quad span is hidden. A level of detail skips grid lines, so it checks the
        // grid points between its corners too, and no visible point inside the span loses its triangles.
        private static bool IsHidden(RockFaceStrip strip, int firstColumn, int lastColumn, int firstRow, int lastRow)
        {
            for (int column = firstColumn; column <= lastColumn; column++)
            {
                int gridColumn = column % strip.Columns;
                for (int row = firstRow; row <= lastRow; row++)
                {
                    if (strip.Active[strip.Index(gridColumn, row)]) return false;
                }
            }
            return true;
        }

#if UNITY_EDITOR
        /// <summary>The raw data of a finished mesh, for the generated mesh cache.</summary>
        public sealed class MeshRecord
        {
            public string Name;
            public int VertexCount;
            public int RockCount;
            public int CoverCount;
            public bool Small;
            public bool Readable;
            public Bounds Bounds;
            public byte[] Vertices;
            public byte[] Indices;
        }

        private static Dictionary<Mesh, MeshRecord> _recording;

        /// <summary>Starts to keep the data of the meshes that this class finishes. Main thread.</summary>
        public static void BeginRecording()
        {
            _recording = new Dictionary<Mesh, MeshRecord>();
        }

        /// <summary>Ends the recording and returns the data of each mesh.</summary>
        public static Dictionary<Mesh, MeshRecord> EndRecording()
        {
            Dictionary<Mesh, MeshRecord> records = _recording;
            _recording = null;
            return records;
        }

        private static void Record(Mesh mesh, int vertexCount, int rockCount, int coverCount, bool small, bool readable)
        {
            int indexCount = rockCount + coverCount;
            var record = new MeshRecord
            {
                Name = mesh.name, VertexCount = vertexCount, RockCount = rockCount, CoverCount = coverCount,
                Small = small, Readable = readable, Bounds = mesh.bounds
            };
            record.Vertices = MemoryMarshal.AsBytes(new System.ReadOnlySpan<Vertex>(_vertices, 0, vertexCount)).ToArray();
            record.Indices = small
                ? MemoryMarshal.AsBytes(new System.ReadOnlySpan<ushort>(_shortIndices, 0, indexCount)).ToArray()
                : MemoryMarshal.AsBytes(new System.ReadOnlySpan<int>(_indices, 0, indexCount)).ToArray();
            _recording[mesh] = record;
        }
#endif

        /// <summary>Starts a mesh from the raw vertices of a record. <see cref="EndMesh"/> ends it.</summary>
        public static Mesh BeginMesh(string name, int vertexCount, byte[] vertices, int vertexBytes)
        {
            Mesh mesh = new() { name = name, hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertexBufferParams(vertexCount, Layout);
            mesh.SetVertexBufferData(vertices, 0, 0, vertexBytes);
            return mesh;
        }

        /// <summary>Ends a mesh with the raw indices of a record.</summary>
        public static void EndMesh(Mesh mesh, int rockCount, int coverCount, bool small, byte[] indices, int indexBytes,
            Bounds bounds, bool readable, IReadOnlyList<Bounds> detailAreas)
        {
            const MeshUpdateFlags Flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;
            int indexCount = rockCount + coverCount;
            mesh.SetIndexBufferParams(indexCount, small ? IndexFormat.UInt16 : IndexFormat.UInt32);
            mesh.SetIndexBufferData(indices, 0, 0, indexBytes, Flags);
            mesh.subMeshCount = coverCount > 0 ? 2 : 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, rockCount), Flags);
            if (coverCount > 0) mesh.SetSubMesh(1, new SubMeshDescriptor(rockCount, coverCount), Flags);
            mesh.bounds = bounds;
            mesh.UploadMeshData(!readable && !InAreas(bounds, detailAreas));
        }

        private static Mesh FinishMesh(int vertexCount, int rockCount, int coverCount, string name, bool readable,
            IReadOnlyList<Bounds> detailAreas)
        {
            Vertex[] vertices = _vertices;
            // The cover triangles follow the rock triangles in the index buffer.
            int indexCount = rockCount + coverCount;
            int[] indices = Reserve(ref _indices, indexCount, true);
            System.Array.Copy(_coverIndices, 0, indices, rockCount, coverCount);
            bool small = vertexCount <= ushort.MaxValue;
            Mesh mesh = new() { name = name, hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertexBufferParams(vertexCount, Layout);
            mesh.SetVertexBufferData(vertices, 0, 0, vertexCount);
            if (small)
            {
                ushort[] shortIndices = Reserve(ref _shortIndices, indexCount);
                for (int index = 0; index < indexCount; index++)
                {
                    shortIndices[index] = (ushort)indices[index];
                }
                mesh.SetIndexBufferParams(indexCount, IndexFormat.UInt16);
                mesh.SetIndexBufferData(shortIndices, 0, 0, indexCount);
            }
            else
            {
                mesh.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
                mesh.SetIndexBufferData(indices, 0, 0, indexCount);
            }
            mesh.subMeshCount = coverCount > 0 ? 2 : 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, rockCount));
            if (coverCount > 0)
            {
                mesh.SetSubMesh(1, new SubMeshDescriptor(rockCount, coverCount));
            }
            mesh.RecalculateBounds();
            // The renderer needs only the GPU copy. This frees the CPU copy of the vertices and indices, except for a
            // mesh that a collider or a cave entrance reads.
#if UNITY_EDITOR
            if (_recording != null) Record(mesh, vertexCount, rockCount, coverCount, small, readable);
#endif
            mesh.UploadMeshData(!readable && !InAreas(mesh.bounds, detailAreas));
            return mesh;
        }

        private static bool InAreas(Bounds bounds, IReadOnlyList<Bounds> areas)
        {
            if (areas == null) return false;
            foreach (Bounds area in areas)
            {
                if (area.Intersects(bounds)) return true;
            }
            return false;
        }
    }
}
