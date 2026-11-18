using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine.Splines;
#endif

// The player build step (GeneratedMeshPlayerBuild) ships the cache files of the generators.
[assembly: InternalsVisibleTo("LoogaSoft.Terrain.Editor")]

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// Keeps generated rock meshes in the Library folder, so an editor start can load them and does not build them. One
    /// file holds the meshes of one generator. A player build ships the files of the current state, so a player loads
    /// the meshes and does not build them at the scene load. A water body writes its file only for a player build, with
    /// an own format and key (LoogaWaterBody).
    /// </summary>
    /// <remarks>
    /// The key of a file covers the generator code (<see cref="GeneratedReloadHandover.CodeVersion"/>), the serialized
    /// settings and the spline of the generator, and the
    /// heights and holes of the terrains near it. The cached mesh is valid when the key is the same. A stamp that changes
    /// the ground under a generator changes the heights there, so the key also covers the stamps in practice. A player
    /// does not check the key: the build ships only files whose key is the same as the open scene.
    /// </remarks>
    internal static class GeneratedMeshCache
    {
        /// <summary>Folder of the shipped files in the StreamingAssets of a player.</summary>
        public const string PlayerFolder = "LoogaGeneratedMeshes";

        // Reused for the reads, so the large buffers do not grow the managed heap for each mesh.
        private static byte[] _readBuffer = new byte[1 << 20];

#if UNITY_EDITOR
        private const string Folder = "Library/LoogaGeneratedMeshes";
        private const int FormatVersion = 1;
        // Terrains this far from the spline count for the key, in meters.
        private const float KeyMargin = 200f;

        private static readonly Regex InstanceIds = new("\"instanceID\":\\s*-?\\d+", RegexOptions.Compiled);
        private static readonly Dictionary<TerrainData, Hash128> TerrainHashes = new();
        // File writes on worker threads that did not end yet. The player build waits for them.
        private static readonly List<Task> Writes = new();
#endif

        /// <summary>
        /// The shipped file of a generator in a player, or null in the editor and when the build shipped no file.
        /// </summary>
        public static string PlayerPath(Component owner)
        {
            if (Application.isEditor) return null;
            string path = Path.Combine(Application.streamingAssetsPath, PlayerFolder, PlayerFileName(owner));
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// The name of the shipped file of a generator: a hash of its scene, its hierarchy path and its type. The editor
        /// and the player give the same name. Objects tagged EditorOnly do not count, because a build removes them.
        /// </summary>
        public static string PlayerFileName(Component owner)
        {
            var text = new StringBuilder(owner.gameObject.scene.name);
            AppendPath(text, owner.transform);
            text.Append('|').Append(owner.GetType().FullName);
            return Hash128.Compute(text.ToString()) + ".mesh";
        }

        // Each level holds the name and the count of earlier siblings with the same name.
        private static void AppendPath(StringBuilder text, Transform transform)
        {
            Transform parent = transform.parent;
            int same = 0;
            if (parent)
            {
                AppendPath(text, parent);
                for (int index = 0; index < transform.GetSiblingIndex(); index++)
                {
                    Transform sibling = parent.GetChild(index);
                    if (sibling.name == transform.name && !sibling.CompareTag("EditorOnly"))
                    {
                        same++;
                    }
                }
            }
            else
            {
                foreach (GameObject root in transform.gameObject.scene.GetRootGameObjects())
                {
                    if (root.transform == transform) break;
                    if (root.name == transform.name && !root.CompareTag("EditorOnly"))
                    {
                        same++;
                    }
                }
            }
            text.Append('/').Append(transform.name).Append('#').Append(same);
        }

        /// <summary>A copy of a readable mesh with any vertex layout, for generators outside the rock chunks.</summary>
        public sealed class RawMesh
        {
            private string _name;
            private VertexAttributeDescriptor[] _attributes;
            private int _vertexCount;
            private byte[][] _streams;
            private IndexFormat _format;
            private byte[] _indices;
            private SubMeshDescriptor[] _subMeshes;
            private Bounds _bounds;

            public static RawMesh From(Mesh mesh)
            {
                using Mesh.MeshDataArray array = Mesh.AcquireReadOnlyMeshData(mesh);
                Mesh.MeshData data = array[0];
                var raw = new RawMesh
                {
                    _name = mesh.name,
                    _attributes = mesh.GetVertexAttributes(),
                    _vertexCount = data.vertexCount,
                    _streams = new byte[data.vertexBufferCount][],
                    _format = data.indexFormat,
                    _indices = data.GetIndexData<byte>().ToArray(),
                    _subMeshes = new SubMeshDescriptor[data.subMeshCount],
                    _bounds = mesh.bounds
                };
                for (int stream = 0; stream < raw._streams.Length; stream++)
                {
                    raw._streams[stream] = data.GetVertexData<byte>(stream).ToArray();
                }
                for (int subMesh = 0; subMesh < raw._subMeshes.Length; subMesh++)
                {
                    raw._subMeshes[subMesh] = data.GetSubMesh(subMesh);
                }
                return raw;
            }

            public void Write(BinaryWriter writer)
            {
                writer.Write(_name);
                writer.Write(_attributes.Length);
                foreach (VertexAttributeDescriptor attribute in _attributes)
                {
                    writer.Write((int)attribute.attribute);
                    writer.Write((int)attribute.format);
                    writer.Write(attribute.dimension);
                    writer.Write(attribute.stream);
                }
                writer.Write(_vertexCount);
                writer.Write(_streams.Length);
                foreach (byte[] stream in _streams)
                {
                    writer.Write(stream.Length);
                    writer.Write(stream);
                }
                writer.Write((int)_format);
                writer.Write(_indices.Length);
                writer.Write(_indices);
                writer.Write(_subMeshes.Length);
                foreach (SubMeshDescriptor subMesh in _subMeshes)
                {
                    writer.Write(subMesh.indexStart);
                    writer.Write(subMesh.indexCount);
                    writer.Write(subMesh.baseVertex);
                    WriteBounds(writer, subMesh.bounds);
                }
                WriteBounds(writer, _bounds);
            }

            /// <summary>Reads a mesh that stays readable. Main thread.</summary>
            public static Mesh Read(BinaryReader reader)
            {
                const MeshUpdateFlags Flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;
                var mesh = new Mesh { name = reader.ReadString(), hideFlags = HideFlags.HideAndDontSave };
                var attributes = new VertexAttributeDescriptor[reader.ReadInt32()];
                for (int index = 0; index < attributes.Length; index++)
                {
                    var attribute = (VertexAttribute)reader.ReadInt32();
                    var format = (VertexAttributeFormat)reader.ReadInt32();
                    int dimension = reader.ReadInt32();
                    attributes[index] = new VertexAttributeDescriptor(attribute, format, dimension, reader.ReadInt32());
                }
                mesh.SetVertexBufferParams(reader.ReadInt32(), attributes);
                int streams = reader.ReadInt32();
                for (int stream = 0; stream < streams; stream++)
                {
                    int bytes = ReadBlock(reader);
                    mesh.SetVertexBufferData(_readBuffer, 0, 0, bytes, stream, Flags);
                }
                var format2 = (IndexFormat)reader.ReadInt32();
                int indexBytes = ReadBlock(reader);
                mesh.SetIndexBufferParams(indexBytes / (format2 == IndexFormat.UInt16 ? 2 : 4), format2);
                mesh.SetIndexBufferData(_readBuffer, 0, 0, indexBytes, Flags);
                mesh.subMeshCount = reader.ReadInt32();
                for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                {
                    var descriptor = new SubMeshDescriptor(reader.ReadInt32(), reader.ReadInt32())
                    {
                        baseVertex = reader.ReadInt32(), bounds = ReadBounds(reader)
                    };
                    mesh.SetSubMesh(subMesh, descriptor, Flags);
                }
                mesh.bounds = ReadBounds(reader);
                return mesh;
            }
        }

#if UNITY_EDITOR
        /// <summary>One chunk as a cache file holds it. The meshes are the records of RockFaceChunks.</summary>
        public sealed class ChunkEntry
        {
            public string Terrain;
            public bool Collider;
            public Bounds Bounds;
            public RockFaceChunks.MeshRecord[] Levels;
        }

        [InitializeOnLoadMethod]
        private static void WatchTerrains()
        {
            TerrainCallbacks.heightmapChanged += (terrain, region, synched) => TerrainHashes.Remove(terrain.terrainData);
        }

        [MenuItem("Tools/Looga/Clear Generated Mesh Cache")]
        private static void Clear()
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
        }

        /// <summary>The cache file of a generator.</summary>
        public static string PathOf(Component owner)
        {
            string id = GlobalObjectId.GetGlobalObjectIdSlow(owner).ToString();
            return Folder + "/" + Hash128.Compute(id) + ".mesh";
        }

        /// <summary>The key of the current state of a generator on a spline.</summary>
        public static Hash128 Key(Component owner, SplineContainer spline)
        {
            var hash = new Hash128();
            hash.Append(InstanceIds.Replace(EditorJsonUtility.ToJson(spline), string.Empty));
            hash.Append(spline.transform.localToWorldMatrix.ToString("F4"));
            return Key(owner, SplineRegion(spline), hash);
        }

        /// <summary>The key of a generator with an own value for its other inputs, for the terrains of a region.</summary>
        public static Hash128 Key(Component owner, Rect region, Hash128 extra)
        {
            var hash = new Hash128();
            hash.Append(FormatVersion);
            // The generator code, not the assembly version: a change of other package code keeps the files.
            hash.Append(GeneratedReloadHandover.CodeVersion);
            hash.Append(StateText(owner));
            hash.Append(extra.ToString());
            foreach (UnityEngine.Terrain terrain in Terrains())
            {
                TerrainData data = terrain.terrainData;
                if (!data) continue;
                Vector3 position = terrain.transform.position;
                var rect = new Rect(position.x, position.z, data.size.x, data.size.z);
                if (rect.Overlaps(region)) hash.Append(TerrainHash(data).ToString());
            }
            return hash;
        }

        /// <summary>The serialized state of a component and its transform, without instance IDs.</summary>
        public static string StateText(Component component)
        {
            return InstanceIds.Replace(EditorJsonUtility.ToJson(component), string.Empty) +
                component.transform.localToWorldMatrix.ToString("F4");
        }

        /// <summary>Forgets the terrain hash after a change without a height callback, for example of the holes.</summary>
        public static void Forget(TerrainData data)
        {
            TerrainHashes.Remove(data);
        }

        /// <summary>True when the cache file exists and has this key.</summary>
        public static bool HasKey(string path, Hash128 key)
        {
            using BinaryReader reader = OpenFile(path, key);
            return reader != null;
        }

        /// <summary>Waits until the file writes on worker threads end. Main thread.</summary>
        public static void WaitForWrites()
        {
            Task[] writes;
            lock (Writes)
            {
                writes = Writes.ToArray();
                Writes.Clear();
            }
            Task.WaitAll(writes);
        }

        /// <summary>Writes a cache file with an own format on a worker thread. A later write replaces it.</summary>
        public static void WriteFile(string path, Hash128 key, Action<BinaryWriter> write)
        {
            Track(Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    string temporary = path + "." + Guid.NewGuid().ToString("N");
                    using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    using (var writer = new BinaryWriter(stream))
                    {
                        writer.Write(key.ToString());
                        write(writer);
                    }
                    File.Delete(path);
                    File.Move(temporary, path);
                }
                catch (IOException)
                {
                    // The cache only saves time: the next load builds the meshes.
                }
            }));
        }

        /// <summary>The XZ rectangle of world bounds with the margin of the key.</summary>
        public static Rect BoundsRegion(Bounds bounds)
        {
            return Rect.MinMaxRect(bounds.min.x - KeyMargin, bounds.min.z - KeyMargin, bounds.max.x + KeyMargin,
                bounds.max.z + KeyMargin);
        }

        private static void Track(Task write)
        {
            lock (Writes)
            {
                Writes.RemoveAll(item => item.IsCompleted);
                Writes.Add(write);
            }
        }

        private static Rect SplineRegion(SplineContainer spline)
        {
            Bounds bounds = default;
            bool first = true;
            Matrix4x4 toWorld = spline.transform.localToWorldMatrix;
            foreach (Spline curve in spline.Splines)
            {
                foreach (BezierKnot knot in curve.Knots)
                {
                    Vector3 point = toWorld.MultiplyPoint3x4(knot.Position);
                    if (first) bounds = new Bounds(point, Vector3.zero);
                    else bounds.Encapsulate(point);
                    first = false;
                }
            }
            return BoundsRegion(bounds);
        }

        private static unsafe Hash128 TerrainHash(TerrainData data)
        {
            if (TerrainHashes.TryGetValue(data, out Hash128 known)) return known;
            var hash = new Hash128();
            int resolution = data.heightmapResolution;
            float[,] heights = data.GetHeights(0, 0, resolution, resolution);
            fixed (float* values = heights)
            {
                HashUnsafeUtilities.ComputeHash128(values, (ulong)(heights.Length * sizeof(float)), &hash);
            }
            int holes = data.holesResolution;
            bool[,] surface = data.GetHoles(0, 0, holes, holes);
            fixed (bool* values = surface)
            {
                HashUnsafeUtilities.ComputeHash128(values, (ulong)surface.Length, &hash);
            }
            TerrainHashes[data] = hash;
            return hash;
        }

        /// <summary>Writes a cache file on a worker thread. A later write replaces it.</summary>
        public static void Write(string path, Hash128 key, string meta, List<ChunkEntry> chunks)
        {
            Track(Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    string temporary = path + "." + Guid.NewGuid().ToString("N");
                    using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    using (var writer = new BinaryWriter(stream))
                    {
                        writer.Write(key.ToString());
                        writer.Write(meta);
                        writer.Write(chunks.Count);
                        foreach (ChunkEntry chunk in chunks)
                        {
                            writer.Write(chunk.Terrain);
                            writer.Write(chunk.Collider);
                            WriteBounds(writer, chunk.Bounds);
                            writer.Write(chunk.Levels.Length);
                            foreach (RockFaceChunks.MeshRecord level in chunk.Levels)
                            {
                                writer.Write(level.Name);
                                writer.Write(level.VertexCount);
                                writer.Write(level.RockCount);
                                writer.Write(level.CoverCount);
                                writer.Write(level.Small);
                                writer.Write(level.Readable);
                                WriteBounds(writer, level.Bounds);
                                writer.Write(level.Vertices.Length);
                                writer.Write(level.Vertices);
                                writer.Write(level.Indices.Length);
                                writer.Write(level.Indices);
                            }
                        }
                    }
                    File.Delete(path);
                    File.Move(temporary, path);
                }
                catch (IOException)
                {
                    // The cache only saves time: the next load builds the meshes.
                }
            }));
        }
#endif

        /// <summary>
        /// Opens a cache file with an own format. Returns null when there is no file, or when a key is given and the file
        /// has another key.
        /// </summary>
        public static BinaryReader OpenFile(string path, Hash128? key)
        {
            if (path == null || !File.Exists(path)) return null;
            var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20));
            string stored = reader.ReadString();
            if (!key.HasValue || stored == key.Value.ToString()) return reader;
            reader.Dispose();
            return null;
        }

        /// <summary>All terrains of the open scenes in a stable order. Some are not enabled yet during a scene load.</summary>
        public static UnityEngine.Terrain[] Terrains()
        {
            UnityEngine.Terrain[] terrains =
                UnityEngine.Object.FindObjectsByType<UnityEngine.Terrain>(FindObjectsSortMode.None);
            Array.Sort(terrains, (a, b) => string.CompareOrdinal(a.name, b.name));
            return terrains;
        }

        /// <summary>
        /// Reads a cache file. Returns false when there is no file, or when a key is given and the file has another key.
        /// Main thread.
        /// </summary>
        /// <param name="key">The key of the current state, or null in a player, which ships only valid files.</param>
        /// <param name="detailAreas">World areas where the finest level keeps its CPU copy, as in a build.</param>
        public static bool TryRead(string path, Hash128? key, UnityEngine.Terrain[] terrains,
            IReadOnlyList<Bounds> detailAreas, out string meta, out List<(RockFaceChunks.Chunk Chunk, bool Collider)> chunks)
        {
            meta = null;
            chunks = null;
            if (path == null || !File.Exists(path)) return false;
            var meshes = new List<Mesh>();
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                using var reader = new BinaryReader(stream);
                string stored = reader.ReadString();
                if (key.HasValue && stored != key.Value.ToString()) return false;
                meta = reader.ReadString();
                int count = reader.ReadInt32();
                var result = new List<(RockFaceChunks.Chunk, bool)>(count);
                for (int index = 0; index < count; index++)
                {
                    string terrain = reader.ReadString();
                    bool collider = reader.ReadBoolean();
                    Bounds bounds = ReadBounds(reader);
                    var levels = new Mesh[reader.ReadInt32()];
                    for (int level = 0; level < levels.Length; level++)
                    {
                        levels[level] = ReadMesh(reader, detailAreas);
                        meshes.Add(levels[level]);
                    }
                    int group = terrain.Length == 0 ? -1 : Array.FindIndex(terrains, item => item.name == terrain);
                    if (terrain.Length > 0 && group < 0) throw new InvalidDataException("Missing terrain " + terrain);
                    result.Add((new RockFaceChunks.Chunk { Levels = levels, Bounds = bounds, Group = group }, collider));
                }
                chunks = result;
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidDataException)
            {
                foreach (Mesh mesh in meshes)
                {
                    if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
                }
                return false;
            }
        }

        private static Mesh ReadMesh(BinaryReader reader, IReadOnlyList<Bounds> detailAreas)
        {
            string name = reader.ReadString();
            int vertexCount = reader.ReadInt32();
            int rockCount = reader.ReadInt32();
            int coverCount = reader.ReadInt32();
            bool small = reader.ReadBoolean();
            bool readable = reader.ReadBoolean();
            Bounds bounds = ReadBounds(reader);
            int vertexBytes = ReadBlock(reader);
            Mesh mesh = RockFaceChunks.BeginMesh(name, vertexCount, _readBuffer, vertexBytes);
            int indexBytes = ReadBlock(reader);
            RockFaceChunks.EndMesh(mesh, rockCount, coverCount, small, _readBuffer, indexBytes, bounds, readable,
                detailAreas);
            return mesh;
        }

        private static int ReadBlock(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (_readBuffer.Length < count) _readBuffer = new byte[count + count / 2];
            int read = 0;
            while (read < count)
            {
                int part = reader.Read(_readBuffer, read, count - read);
                if (part == 0) throw new InvalidDataException("Short cache file.");
                read += part;
            }
            return count;
        }

        private static void WriteBounds(BinaryWriter writer, Bounds bounds)
        {
            Vector3 centre = bounds.center, size = bounds.size;
            writer.Write(centre.x);
            writer.Write(centre.y);
            writer.Write(centre.z);
            writer.Write(size.x);
            writer.Write(size.y);
            writer.Write(size.z);
        }

        private static Bounds ReadBounds(BinaryReader reader)
        {
            return new Bounds(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
        }
    }
}
