using System.Collections.Generic;
using System.Linq;
using LoogaSoft.Terrain.Geology;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Roads.Editor
{
    /// <summary>
    /// Far levels of detail of the road slabs. A LODGroup selects a level from the screen size of the whole object, so a
    /// slab of several hundred meters never left its full mesh. Each slab is cut into chunks on a world grid, and each
    /// chunk gets coarser meshes. The slab object keeps its full mesh for the live update and the build comparison, but
    /// its own renderer is off: the chunks draw the slab.
    /// </summary>
    internal static class LoogaRoadSlabLods
    {
        // Side of a chunk cell in meters.
        private const float ChunkSize = 50f;
        // Largest quadric error (squared meters) of each coarser level, and the camera distance where it starts.
        private static readonly double[] LevelErrors = { 0.02 * 0.02, 0.08 * 0.08 };
        private static readonly float[] LevelDistances = { 120f, 350f };
        // A chunk stops drawing at this distance. The roads stay visible over the whole world.
        private const float CullDistance = 20000f;
        // Field of view in degrees that converts the switch distances to LODGroup screen heights.
        private const float ReferenceFieldOfView = 60f;
        // A detail value far outside the real values. A collapse that moves a locked vertex then always costs too much.
        private const float LockedDetail = 1000f;
        internal const string ChunkPrefix = "Chunk ";

        /// <summary>
        /// Makes the chunks and their levels of detail of one slab again from its full mesh. The chunk meshes are
        /// sub-assets of the slab mesh asset.
        /// </summary>
        public static void Apply(Transform slab, Mesh full, Material material, LoogaRoadNetwork network)
        {
            RemoveChunks(slab, full);
            if (slab.TryGetComponent(out MeshRenderer slabRenderer))
            {
                // The chunks take the material of the slab, which can differ from the network material.
                if (slabRenderer.sharedMaterial)
                {
                    material = slabRenderer.sharedMaterial;
                }
                slabRenderer.enabled = false;
            }

            var source = new SourceMesh(full);
            Vector3 origin = slab.position;
            foreach (KeyValuePair<long, List<int>[]> cell in SplitIntoCells(source, origin))
            {
                List<int>[] submeshes = cell.Value;
                var chunkObject = new GameObject(ChunkPrefix + (int)(cell.Key >> 32) + " " + (int)cell.Key);
                chunkObject.transform.SetParent(slab, false);
                chunkObject.isStatic = slab.gameObject.isStatic;
                ChunkMesh chunk = ChunkMesh.Create(source, submeshes);
                var lods = new LOD[LevelErrors.Length + 1];
                for (int level = 0; level <= LevelErrors.Length; level++)
                {
                    Mesh mesh = level == 0 ? chunk.ToMesh() : chunk.Reduce(LevelErrors[level - 1]);
                    mesh.name = full.name + " " + chunkObject.name + " LOD" + level;
                    AssetDatabase.AddObjectToAsset(mesh, full);
                    MeshRenderer renderer = AddLevel(chunkObject.transform, "LOD" + level, mesh, material);
                    network.ApplyGround(renderer);
                    // Provisional heights in falling order: SetLODs rejects equal heights. The real heights need the
                    // group size, which the group knows only after it has its renderers.
                    lods[level] = new LOD(0.5f / (level + 1), new Renderer[] { renderer });
                }
                var group = chunkObject.AddComponent<LODGroup>();
                group.fadeMode = LODFadeMode.None;
                group.SetLODs(lods);
                group.RecalculateBounds();
                SetSwitchDistances(group, lods);
            }
            EditorUtility.SetDirty(full);
        }

        /// <summary>
        /// Makes the chunks of every slab of the network again from the slab meshes, without a new build of the slabs.
        /// </summary>
        public static string ApplyAll(LoogaRoadNetwork network)
        {
            if (!network.SlabRoot) return "The road network has no slab root.";

            int slabs = 0;
            int chunks = 0;
            foreach (Transform slab in network.SlabRoot)
            {
                if (!slab.TryGetComponent(out MeshFilter filter) || !filter.sharedMesh) continue;

                Apply(slab, filter.sharedMesh, network.SlabMaterial, network);
                slabs++;
                chunks += slab.GetComponentsInChildren<LODGroup>().Length;
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(network.gameObject.scene);
            return slabs + " slabs, " + chunks + " chunks with " + (LevelErrors.Length + 1) + " levels.";
        }

        [MenuItem("CONTEXT/LoogaRoadNetwork/Build Slab Levels Of Detail")]
        private static void ApplyAllFromMenu(MenuCommand command)
        {
            Debug.Log(ApplyAll((LoogaRoadNetwork)command.context));
        }

        // The old chunk objects and their meshes go before a new build of the chunks.
        private static void RemoveChunks(Transform slab, Mesh full)
        {
            for (int index = slab.childCount - 1; index >= 0; index--)
            {
                Transform child = slab.GetChild(index);
                if (child.name.StartsWith(ChunkPrefix))
                {
                    Object.DestroyImmediate(child.gameObject);
                }
            }
            string path = AssetDatabase.GetAssetPath(full);
            if (string.IsNullOrEmpty(path)) return;

            foreach (Object item in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
            {
                if (item is Mesh && item != full)
                {
                    Object.DestroyImmediate(item, true);
                }
            }
        }

        private static MeshRenderer AddLevel(Transform chunk, string name, Mesh mesh, Material material)
        {
            var level = new GameObject(name);
            level.transform.SetParent(chunk, false);
            level.isStatic = chunk.gameObject.isStatic;
            level.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = level.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = Enumerable.Repeat(material, mesh.subMeshCount).ToArray();
            // As the full slab: a 10 cm slab casts no useful shadow.
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            return renderer;
        }

        // LODGroup heights are screen shares of the group size. The heights come from distances, so a small chunk at
        // a slab end switches at the same distance as a full chunk.
        private static void SetSwitchDistances(LODGroup group, LOD[] lods)
        {
            float size = Mathf.Max(group.size, 0.01f);
            float screen = 2f * Mathf.Tan(0.5f * ReferenceFieldOfView * Mathf.Deg2Rad);
            for (int level = 0; level < lods.Length; level++)
            {
                float distance = level < LevelDistances.Length ? LevelDistances[level] : CullDistance;
                lods[level].screenRelativeTransitionHeight = size / (distance * screen);
            }
            group.SetLODs(lods);
        }

        // The triangles of each world grid cell, by submesh. A triangle belongs to the cell of its center.
        private static Dictionary<long, List<int>[]> SplitIntoCells(SourceMesh source, Vector3 origin)
        {
            var cells = new Dictionary<long, List<int>[]>();
            for (int submesh = 0; submesh < source.Submeshes.Length; submesh++)
            {
                int[] triangles = source.Submeshes[submesh];
                for (int corner = 0; corner < triangles.Length; corner += 3)
                {
                    Vector3 center = (source.Vertices[triangles[corner]] + source.Vertices[triangles[corner + 1]] +
                        source.Vertices[triangles[corner + 2]]) / 3f + origin;
                    long key = ((long)Mathf.FloorToInt(center.x / ChunkSize) << 32) |
                        (uint)Mathf.FloorToInt(center.z / ChunkSize);
                    if (!cells.TryGetValue(key, out List<int>[] lists))
                    {
                        lists = new List<int>[source.Submeshes.Length];
                        cells.Add(key, lists);
                    }
                    lists[submesh] ??= new List<int>();
                    lists[submesh].Add(triangles[corner]);
                    lists[submesh].Add(triangles[corner + 1]);
                    lists[submesh].Add(triangles[corner + 2]);
                }
            }
            return cells;
        }

        // The vertex data of the full slab mesh. The normals and tangents come from the full mesh, so the chunk borders
        // shade without a seam.
        private sealed class SourceMesh
        {
            public readonly Vector3[] Vertices;
            public readonly Vector3[] Normals;
            public readonly Vector4[] Tangents;
            public readonly List<Vector2> Uv0 = new List<Vector2>();
            public readonly List<Vector4> Road = new List<Vector4>();
            public readonly Color32[] Joins;
            public readonly int[][] Submeshes;

            public SourceMesh(Mesh mesh)
            {
                Vertices = mesh.vertices;
                Normals = mesh.normals;
                Tangents = mesh.tangents;
                mesh.GetUVs(0, Uv0);
                mesh.GetUVs(1, Road);
                Joins = mesh.colors32;
                Submeshes = new int[mesh.subMeshCount][];
                for (int submesh = 0; submesh < Submeshes.Length; submesh++)
                {
                    Submeshes[submesh] = mesh.GetTriangles(submesh);
                }
            }
        }

        // The vertices and triangles of one chunk. The vertices of the chunk border are copies of the slab vertices, so
        // the neighbor chunks meet at the same positions.
        private sealed class ChunkMesh
        {
            private readonly List<int> _sourceIndices = new List<int>();
            private readonly List<int[]> _submeshes = new List<int[]>();
            private SourceMesh _source;

            public static ChunkMesh Create(SourceMesh source, List<int>[] submeshes)
            {
                var chunk = new ChunkMesh { _source = source };
                var remap = new Dictionary<int, int>();
                foreach (List<int> triangles in submeshes)
                {
                    if (triangles == null) continue;

                    var local = new int[triangles.Count];
                    for (int corner = 0; corner < triangles.Count; corner++)
                    {
                        if (!remap.TryGetValue(triangles[corner], out int index))
                        {
                            index = chunk._sourceIndices.Count;
                            remap.Add(triangles[corner], index);
                            chunk._sourceIndices.Add(triangles[corner]);
                        }
                        local[corner] = index;
                    }
                    chunk._submeshes.Add(local);
                }
                return chunk;
            }

            public Mesh ToMesh()
            {
                return Build(_submeshes);
            }

            // Each submesh reduces alone. The vertices of its open edges stay: the slab edge at the ground, the chunk
            // border and the tile border between submeshes. The neighbor chunks and levels then meet without cracks.
            public Mesh Reduce(double errorLimit)
            {
                var reduced = new List<int[]>();
                var positions = new NativeArray<float3>(_sourceIndices.Count, Allocator.TempJob);
                var details = new NativeArray<float>(_sourceIndices.Count, Allocator.TempJob);
                try
                {
                    for (int index = 0; index < _sourceIndices.Count; index++)
                    {
                        positions[index] = _source.Vertices[_sourceIndices[index]];
                    }
                    foreach (int[] triangles in _submeshes)
                    {
                        LockOpenEdges(triangles, details);
                        using var input = new NativeArray<int>(triangles, Allocator.TempJob);
                        NativeList<int> result = RockDecimator.Reduce(positions, details, input, errorLimit);
                        reduced.Add(result.AsArray().ToArray());
                        result.Dispose();
                    }
                }
                finally
                {
                    positions.Dispose();
                    details.Dispose();
                }
                return Build(reduced);
            }

            // Each locked vertex has its own detail value, so no collapse can move it.
            private static void LockOpenEdges(int[] triangles, NativeArray<float> details)
            {
                for (int index = 0; index < details.Length; index++)
                {
                    details[index] = 0f;
                }
                var edges = new Dictionary<long, int>();
                for (int corner = 0; corner < triangles.Length; corner += 3)
                {
                    for (int side = 0; side < 3; side++)
                    {
                        long key = EdgeKey(triangles[corner + side], triangles[corner + (side + 1) % 3]);
                        edges[key] = edges.TryGetValue(key, out int count) ? count + 1 : 1;
                    }
                }
                foreach (KeyValuePair<long, int> edge in edges)
                {
                    if (edge.Value != 1) continue;

                    int a = (int)(edge.Key >> 32);
                    int b = (int)edge.Key;
                    details[a] = LockedDetail + a;
                    details[b] = LockedDetail + b;
                }
            }

            private static long EdgeKey(int a, int b)
            {
                return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            }

            // A mesh with only the vertices that the triangles use.
            private Mesh Build(List<int[]> submeshes)
            {
                var used = new Dictionary<int, int>();
                var order = new List<int>();
                var compact = new List<int[]>();
                foreach (int[] triangles in submeshes)
                {
                    var local = new int[triangles.Length];
                    for (int corner = 0; corner < triangles.Length; corner++)
                    {
                        if (!used.TryGetValue(triangles[corner], out int index))
                        {
                            index = order.Count;
                            used.Add(triangles[corner], index);
                            order.Add(triangles[corner]);
                        }
                        local[corner] = index;
                    }
                    compact.Add(local);
                }

                SourceMesh source = _source;
                var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(Pick(source.Vertices, order));
                // SaveMesh writes all of these, but an older slab asset can miss a stream.
                if (source.Normals.Length > 0)
                {
                    mesh.SetNormals(Pick(source.Normals, order));
                }
                if (source.Tangents.Length > 0)
                {
                    mesh.SetTangents(Pick(source.Tangents, order));
                }
                if (source.Uv0.Count > 0)
                {
                    mesh.SetUVs(0, Pick(source.Uv0, order));
                }
                if (source.Road.Count > 0)
                {
                    mesh.SetUVs(1, Pick(source.Road, order));
                }
                if (source.Joins.Length > 0)
                {
                    mesh.SetColors(Pick(source.Joins, order));
                }
                mesh.subMeshCount = compact.Count;
                for (int submesh = 0; submesh < compact.Count; submesh++)
                {
                    mesh.SetTriangles(compact[submesh], submesh);
                }
                mesh.RecalculateBounds();
                return mesh;
            }

            // The values of the chunk vertices in the given order, from a stream of the full slab mesh.
            private T[] Pick<T>(IReadOnlyList<T> stream, List<int> order)
            {
                var values = new T[order.Count];
                for (int index = 0; index < order.Count; index++)
                {
                    values[index] = stream[_sourceIndices[order[index]]];
                }
                return values;
            }
        }
    }
}
