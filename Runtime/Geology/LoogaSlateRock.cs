using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>
    /// A slate monolith from one seed: geological layout on the CPU, signed distance field on the GPU, sharp dual contouring,
    /// surface masks, levels of detail and baked slate maps. The rock is in meters, around the transform.
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("Looga/Slate Rock")]
    public sealed class LoogaSlateRock : MonoBehaviour
    {
        private const string OutputName = "Slate Rock Output";
        private const string FieldComputeName = "LoogaSlateField";
        private const string ShaderName = "Looga/Slate Rock";
        private const int ThreadGroupSize = 4;
        // Thread groups in one dispatch along Z. A long dispatch keeps the GPU from other work.
        private const int SliceGroups = 8;
        private const long MaximumSamples = 40000000;
        // Room around the hull for the moves of the layers, in normalized units.
        private const float GridMargin = 0.13f;
        private const float OcclusionStep = 0.012f;
        private const float RidgeError = 0.04f;

        private static readonly VertexAttributeDescriptor[] VertexLayout =
        {
            new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new(VertexAttribute.Normal, VertexAttributeFormat.Float16, 4),
            new(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4)
        };

        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex
        {
            public float3 Position;
            public half4 Normal;
            public Color32 Color;
        }

        [SerializeField] private SlateRockSettings _settings = SlateRockSettings.Default;
        [SerializeField] private SlateSurfaceSettings _surface = SlateSurfaceSettings.Default;
        [SerializeField, Min(0.01f), Tooltip("Grid cell size, in meters.")] private float _cellSize = 0.13f;
        [SerializeField, Tooltip("Vertex placement. Sharp keeps the edges of the seams.")]
        private RockVertexPlacement _placement = RockVertexPlacement.Sharp;
        [SerializeField, Range(0f, 90f), Tooltip("A larger turn between two faces is a hard edge.")] private float _creaseAngle = 35f;
        [SerializeField] private int[] _levelTriangles = { 300000, 100000, 30000, 6000 };
        [SerializeField] private float[] _levelErrors = { 0.005f, 0.03f, 0.1f, 0.3f };
        [SerializeField] private float[] _levelScreenHeights = { 0.5f, 0.2f, 0.06f, 0.01f };

        public SlateRockSettings Settings
        {
            get => _settings;
            set => _settings = value;
        }

        public SlateSurfaceSettings Surface
        {
            get => _surface;
            set => _surface = value;
        }

        public float CellSize
        {
            get => _cellSize;
            set => _cellSize = value;
        }

        public RockVertexPlacement Placement
        {
            get => _placement;
            set => _placement = value;
        }

        /// <summary>Text about the last build.</summary>
        public string LastReport { get; private set; }

        private void OnDestroy()
        {
            ClearOutput();
        }

        /// <summary>Builds the rock and replaces the old output.</summary>
        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            var layout = new SlateRockLayout(_settings);
            float scale = _settings.WorldScale;
            float cell = _cellSize / scale;
            float3 reach = (float3)(Vector3)_settings.HalfExtents + GridMargin;
            var size = (int3)math.ceil(2f * reach / cell) + 1;
            if ((long)size.x * size.y * size.z > MaximumSamples)
            {
                Debug.LogError($"Looga Slate Rock: the grid {size} is over the limit of {MaximumSamples} samples. Use a larger cell size.", this);
                return;
            }

            NativeArray<float> field = BuildField(layout, size, -reach, cell);
            int islands = RockDualContour.CleanIslands(field, size);
            RockDualContour.Build(field, size, _placement, out NativeList<float3> positions, out NativeList<int> indices);

            var normals = new NativeArray<float3>(positions.Length, Allocator.TempJob);
            var masks = new NativeArray<float3>(positions.Length, Allocator.TempJob);
            new SlateRockMaskJob
            {
                Field = field, Positions = positions.AsArray(), Triangles = indices.AsArray(), Normals = normals, Masks = masks, Size = size,
                CellSize = _cellSize, OcclusionStep = OcclusionStep * scale
            }.Run();

            var vertices = new NativeArray<Vertex>(positions.Length, Allocator.TempJob);
            var points = new NativeArray<float3>(positions.Length, Allocator.TempJob);
            var details = new NativeArray<float>(positions.Length, Allocator.TempJob);
            float3 origin = -reach * scale;
            for (int index = 0; index < positions.Length; index++)
            {
                float3 point = origin + positions[index] * _cellSize;
                float3 normal = normals[index];
                float3 mask = masks[index];
                points[index] = point;
                // Ridges and creases cost more to remove, so flat faces reduce first.
                details[index] = math.max(mask.x, mask.y) * RidgeError;
                vertices[index] = new Vertex
                {
                    Position = point, Normal = new half4((half)normal.x, (half)normal.y, (half)normal.z, (half)1f),
                    Color = new Color32((byte)(mask.x * 255f), (byte)(mask.y * 255f), (byte)(mask.z * 255f), 255)
                };
            }

            ClearOutput();
            Material material = NewMaterial();
            string levels = BuildLevels(points, details, vertices, indices.AsArray(), material);
            LastReport = $"Grid {size.x} x {size.y} x {size.z}, {layout.Layers.Length} layers, {layout.Joints.Length} joints, " +
                         $"{layout.Chips.Length} chips, {islands} islands removed, {indices.Length / 3} source triangles. {levels}";

            normals.Dispose();
            masks.Dispose();
            vertices.Dispose();
            points.Dispose();
            details.Dispose();
            positions.Dispose();
            indices.Dispose();
            field.Dispose();
        }

        private NativeArray<float> BuildField(SlateRockLayout layout, int3 size, float3 origin, float cell)
        {
            var compute = Resources.Load<ComputeShader>(FieldComputeName);
            if (compute == null) throw new InvalidOperationException($"Compute shader {FieldComputeName} is missing.");
            int kernel = compute.FindKernel("SlateField");

            int sampleCount = size.x * size.y * size.z;
            using var field = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sampleCount, sizeof(float));
            using var fence = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float));
            using GraphicsBuffer hull = NewBuffer(layout.HullPlanes);
            using GraphicsBuffer beds = NewBuffer(layout.Beds);
            using GraphicsBuffer gates = NewBuffer(layout.BedGates);
            using GraphicsBuffer layers = NewBuffer(layout.Layers);
            using GraphicsBuffer joints = NewBuffer(layout.Joints);
            using GraphicsBuffer steps = NewBuffer(layout.Steps);
            using GraphicsBuffer chips = NewBuffer(layout.Chips.Length > 0 ? layout.Chips : new SlateChip[1]);

            compute.SetBuffer(kernel, "_Field", field);
            compute.SetBuffer(kernel, "_HullPlanes", hull);
            compute.SetBuffer(kernel, "_Beds", beds);
            compute.SetBuffer(kernel, "_BedGates", gates);
            compute.SetBuffer(kernel, "_Layers", layers);
            compute.SetBuffer(kernel, "_Joints", joints);
            compute.SetBuffer(kernel, "_Steps", steps);
            compute.SetBuffer(kernel, "_Chips", chips);
            compute.SetInts("_Size", size.x, size.y, size.z);
            compute.SetVector("_Origin", new Vector4(origin.x, origin.y, origin.z, 0f));
            compute.SetFloat("_Cell", cell);
            compute.SetFloat("_WorldScale", _settings.WorldScale);
            compute.SetVector("_Half", _settings.HalfExtents);
            compute.SetVector("_Undercut", _settings.Undercut);
            compute.SetFloat("_UndercutTop", _settings.UndercutTop);
            compute.SetVector("_HullLean", _settings.HullLean);
            compute.SetInt("_HullPlaneCount", layout.HullPlanes.Length);
            compute.SetInt("_LayerCount", layout.Layers.Length);
            compute.SetInt("_ChipCount", layout.Chips.Length);
            compute.SetFloat("_CreaseWidth", _settings.CreaseWidth);
            compute.SetFloat("_CreaseDepth", _settings.CreaseDepth);
            compute.SetFloat("_SeamWander", _settings.SeamWander);
            compute.SetInt("_Seed", _settings.Seed);

            int3 groups = (size + ThreadGroupSize - 1) / ThreadGroupSize;
            for (int group = 0; group < groups.z; group += SliceGroups)
            {
                compute.SetInt("_SliceStart", group * ThreadGroupSize);
                compute.Dispatch(kernel, groups.x, groups.y, Mathf.Min(SliceGroups, groups.z - group));
                // The read ends after the slice ends, so the queue of the GPU stays short.
                AsyncGPUReadback.Request(fence).WaitForCompletion();
            }

            var values = new NativeArray<float>(sampleCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            AsyncGPUReadbackRequest request = AsyncGPUReadback.RequestIntoNativeArray(ref values, field);
            request.WaitForCompletion();
            if (request.hasError)
            {
                values.Dispose();
                throw new InvalidOperationException("The slate field readback failed.");
            }
            return values;
        }

        private static GraphicsBuffer NewBuffer<T>(T[] data) where T : struct
        {
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, data.Length, Marshal.SizeOf<T>());
            buffer.SetData(data);
            return buffer;
        }

        private Material NewMaterial()
        {
            SlateSurfaceMaps maps = SlateSurfaceBaker.Bake(_surface);
            var material = new Material(Shader.Find(ShaderName)) { name = "Slate Rock", hideFlags = HideFlags.DontSave };
            material.SetTexture("_AlbedoMap", maps.Albedo);
            material.SetTexture("_NormalMap", maps.Normal);
            material.SetTexture("_DetailMap", maps.Detail);
            return material;
        }

        private string BuildLevels(NativeArray<float3> points, NativeArray<float> details, NativeArray<Vertex> vertices, NativeArray<int> source,
            Material material)
        {
            int levelCount = _levelErrors.Length;
            var levels = new LOD[levelCount];
            var output = new GameObject(OutputName) { hideFlags = HideFlags.DontSave };
            output.transform.SetParent(transform, false);
            var text = new System.Text.StringBuilder();
            NativeList<int> previous = default;
            for (int level = 0; level < levelCount; level++)
            {
                double errorLimit = _levelErrors[level] * (double)_levelErrors[level];
                NativeArray<int> input = level == 0 ? source : previous.AsArray();
                NativeList<int> reduced = RockDecimator.Reduce(points, details, input, errorLimit, _levelTriangles[level]);
                if (previous.IsCreated) previous.Dispose();
                previous = reduced;

                Mesh mesh = CreateMesh($"Slate LOD{level}", points, vertices, reduced.AsArray(), _creaseAngle);
                var child = new GameObject($"LOD{level}") { hideFlags = HideFlags.DontSave };
                child.transform.SetParent(output.transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = mesh;
                var meshRenderer = child.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterial = material;
                levels[level] = new LOD(_levelScreenHeights[level], new Renderer[] { meshRenderer });
                text.Append($"LOD{level} {mesh.GetIndexCount(0) / 3} tris. ");
            }
            if (previous.IsCreated) previous.Dispose();
            output.AddComponent<LODGroup>().SetLODs(levels);
            return text.ToString();
        }

        private static Mesh CreateMesh(string meshName, NativeArray<float3> points, NativeArray<Vertex> vertices, NativeArray<int> triangles,
            float creaseAngle)
        {
            var kept = new NativeList<Vertex>(vertices.Length, Allocator.TempJob);
            var indices = new NativeArray<int>(triangles.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            new CreaseJob
            {
                Points = points, Vertices = vertices, Triangles = triangles, Kept = kept, Indices = indices,
                CreaseCosine = math.cos(math.radians(creaseAngle))
            }.Run();

            var mesh = new Mesh { name = meshName, hideFlags = HideFlags.DontSave };
            mesh.SetVertexBufferParams(kept.Length, VertexLayout);
            mesh.SetVertexBufferData(kept.AsArray(), 0, 0, kept.Length);
            mesh.SetIndexBufferParams(indices.Length, IndexFormat.UInt32);
            mesh.SetIndexBufferData(indices, 0, 0, indices.Length);
            mesh.subMeshCount = 1;
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, indices.Length));
            mesh.RecalculateBounds();
            kept.Dispose();
            indices.Dispose();
            return mesh;
        }

        // A vertex on a hard edge splits into one copy for each side of the edge, with the mean normal of the faces on that side.
        [BurstCompile]
        private struct CreaseJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Points;
            [ReadOnly] public NativeArray<Vertex> Vertices;
            [ReadOnly] public NativeArray<int> Triangles;
            [WriteOnly] public NativeArray<int> Indices;
            public NativeList<Vertex> Kept;
            public float CreaseCosine;

            public void Execute()
            {
                int cornerCount = Triangles.Length;
                var first = new NativeArray<int>(Vertices.Length, Allocator.Temp);
                var next = new NativeArray<int>(cornerCount, Allocator.Temp);
                var faces = new NativeArray<float3>(cornerCount / 3, Allocator.Temp);
                // The copies of a source vertex form a list. Index + 1, so 0 is the end.
                var firstCopy = new NativeArray<int>(Vertices.Length, Allocator.Temp);
                var nextCopy = new NativeList<int>(Vertices.Length, Allocator.Temp);

                for (int corner = 0; corner < cornerCount; corner++)
                {
                    int vertex = Triangles[corner];
                    next[corner] = first[vertex];
                    first[vertex] = corner + 1;
                }
                for (int face = 0; face < faces.Length; face++)
                {
                    float3 a = Points[Triangles[face * 3]];
                    faces[face] = math.cross(Points[Triangles[face * 3 + 1]] - a, Points[Triangles[face * 3 + 2]] - a);
                }

                for (int corner = 0; corner < cornerCount; corner++)
                {
                    int vertex = Triangles[corner];
                    Vertex copy = Vertices[vertex];
                    float3 own = math.normalizesafe(faces[corner / 3]);
                    float3 sum = float3.zero;
                    bool hard = false;
                    for (int link = first[vertex]; link != 0; link = next[link - 1])
                    {
                        float3 face = faces[(link - 1) / 3];
                        if (math.dot(own, math.normalizesafe(face)) >= CreaseCosine) sum += face;
                        else hard = true;
                    }
                    if (hard)
                    {
                        float3 normal = math.normalizesafe(sum, own);
                        copy.Normal = new half4((half)normal.x, (half)normal.y, (half)normal.z, (half)1f);
                    }

                    int found = -1;
                    for (int link = firstCopy[vertex]; link != 0; link = nextCopy[link - 1])
                    {
                        Vertex other = Kept[link - 1];
                        if (other.Normal.x.value != copy.Normal.x.value || other.Normal.y.value != copy.Normal.y.value ||
                            other.Normal.z.value != copy.Normal.z.value) continue;
                        found = link - 1;
                        break;
                    }
                    if (found < 0)
                    {
                        found = Kept.Length;
                        Kept.Add(copy);
                        nextCopy.Add(firstCopy[vertex]);
                        firstCopy[vertex] = found + 1;
                    }
                    Indices[corner] = found;
                }
            }
        }

        private void ClearOutput()
        {
            Transform output = transform.Find(OutputName);
            if (output == null) return;
            var seen = new HashSet<UnityEngine.Object>();
            foreach (MeshRenderer item in output.GetComponentsInChildren<MeshRenderer>())
            {
                Material material = item.sharedMaterial;
                if (material == null || !seen.Add(material)) continue;
                DestroyObject(material.GetTexture("_AlbedoMap"));
                DestroyObject(material.GetTexture("_NormalMap"));
                DestroyObject(material.GetTexture("_DetailMap"));
                DestroyObject(material);
            }
            foreach (MeshFilter filter in output.GetComponentsInChildren<MeshFilter>()) DestroyObject(filter.sharedMesh);
            DestroyObject(output.gameObject);
        }

        private static void DestroyObject(UnityEngine.Object item)
        {
            if (item == null) return;
            if (Application.isPlaying) Destroy(item);
            else DestroyImmediate(item);
        }
    }
}
