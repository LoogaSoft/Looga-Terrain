using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Geology
{
    /// <summary>Times and sizes of the last rock build. All counts come from the final mesh objects.</summary>
    public sealed class RockBuildReport
    {
        public Vector3Int GridSize;
        public double FieldSeconds;
        public double ContourSeconds;
        public double VertexSeconds;
        public double[] LevelSeconds;
        public double MeshSeconds;
        public int SourceTriangles;
        public int RemovedIslands;
        public int RemovedNeckedParts;
        public int[] LevelTriangles;
        public int[] LevelVertices;
        public Vector3Int VolumeSize;

        public override string ToString()
        {
            var text = new StringBuilder();
            text.Append($"Grid {GridSize.x} x {GridSize.y} x {GridSize.z}, source {SourceTriangles} triangles, ");
            text.Append($"{RemovedIslands} islands removed, {RemovedNeckedParts} necked parts removed. ");
            text.Append($"Field (GPU + readback) {FieldSeconds * 1000.0:F1} ms, ");
            text.Append($"contour {ContourSeconds * 1000.0:F1} ms, vertex data {VertexSeconds * 1000.0:F1} ms. ");
            for (int level = 0; level < LevelTriangles.Length; level++)
            {
                text.Append($"LOD{level} {LevelTriangles[level]} tris ");
                text.Append($"{LevelVertices[level]} verts in {LevelSeconds[level] * 1000.0:F0} ms. ");
            }
            text.Append($"Meshes {MeshSeconds * 1000.0:F1} ms.");
            if (VolumeSize.x > 0) text.Append($" Fracture volume {VolumeSize.x} x {VolumeSize.y} x {VolumeSize.z}.");
            return text.ToString();
        }
    }

    /// <summary>The shape of a rock: a base of convex blocks and an ordered list of field passes.</summary>
    [Serializable]
    public sealed class RockRecipe
    {
        [Tooltip("Full size of the box that holds the blocks, in meters.")]
        public Vector3 Size = new(18f, 11f, 13f);
        [Range(1, 8), Tooltip("Number of convex blocks in the rock.")]
        public int BlockCount = 3;
        [Range(8, 40), Tooltip("Number of faces of each block before the passes.")]
        public int FacesPerBlock = 14;
        public int Seed = 7;
        [Min(0.02f), Tooltip("Distance between two field samples, in meters. Half the size gives 8 times the samples.")]
        public float CellSize = 0.1f;
        [Min(0f), Tooltip("Cuts and carves stop this far below the base surface, in meters, so a solid core remains. 0 = no limit.")]
        public float MaxCarve;
        [Min(0f), Tooltip("A part that hangs on the rock through a neck narrower than this, in meters, is removed with its " +
                          "neck. Thin edges and ridges stay. 0 = off.")]
        public float NeckWidth = 0.3f;
        [Tooltip("Finite fractures. A pass of the kind Fractures applies them.")]
        public List<RockFracture> Fractures = new();
        [Tooltip("Families of random fractures, added to Fractures at each build. Each fracture runs on to an older fracture " +
                 "or out of the rock, so the cracks meet in T junctions. A pass of the kind Fractures applies them.")]
        public List<RockFractureSet> FractureSets = new();
        [Min(0f), Tooltip("Distance between two samples of the fracture volume, in meters. Rock shaders read the volume to put " +
                          "veins, stains and block tones on the fractures. 0 = no volume.")]
        public float FractureVolumeCell;
        [Tooltip("Fractures that divide the first base box into solid blocks. Blocks need Block Settings.")]
        public List<RockFracture> BlockFractures = new();
        public RockBlockSettings BlockSettings;
        [Min(0), Tooltip("The first block fractures divide the rock into this many fractures' worth of large masses that move as one.")]
        public int MassFractures;
        [Tooltip("Depth maps of the box faces, for example from a picture. They set how far each mass moves into the rock.")]
        public List<RockFaceDepth> FaceDepths = new();
        [Tooltip("Wedges cut into the blocks. Center is on the face, Normal is across, Step is depth, Crack Width is width.")]
        public List<RockFracture> BlockChannels = new();
        [Tooltip("Large flat faces cut from the base before the passes.")]
        public List<RockBaseSlice> BaseSlices = new();
        [Tooltip("Broad soft bulges of the base, so large faces are not flat.")]
        public List<RockBaseBulge> BaseBulges = new();
        [Tooltip("Authored masses of the rock. When this has boxes, they replace the random blocks.")]
        public List<RockBaseBox> BaseBoxes = new();
        [Tooltip("Each pass reads the field of the pass before it.")]
        public List<RockPass> Passes = new() { RockPass.DefaultNoise };
        public RockMaskSettings Masks = RockMaskSettings.Default;
        [NonSerialized] public RockImportedField ImportedField;
    }

    /// <summary>
    /// Generates a closed rock from a signed distance field: blocks on a 3D grid, field passes in a compute shader,
    /// dual contouring in Burst, and levels of detail by quadric edge collapse. The rock needs no UVs. Use a triplanar
    /// material.
    /// </summary>
    /// <remarks>The meshes are not saved with the scene. The component builds them again when it starts.</remarks>
    [ExecuteAlways]
    [AddComponentMenu("Looga/Rock Generator")]
    public sealed class LoogaRockGenerator : MonoBehaviour
    {
        private const string OutputName = "Generated rock";
        // Cells of empty grid around the rock. The one-sided differences read two samples past a cell.
        private const int GridMargin = 4;
        // A first build at this many times the cell size finds the real bounds of the rock.
        private const float BoundsCellScale = 4f;
        // Largest grid of one build. A larger build can stop the whole computer: use a larger cell size.
        private const long MaximumSamples = 40_000_000;
        // Largest growth of each end of a set fracture to the fracture that stops it, and the fade at each end, in meters.
        private const float FractureReach = 1.5f;
        private const float FractureEndFade = 0.25f;
        // The blocks fill about this share of the recipe box, so set fractures start on a smaller box near the rock surface.
        private const float FractureBoxShare = 0.85f;
        // Crack line distance of a rock with no cracks, in meters: far from any line.
        private const float NoCrackLine = 10f;
        // Directions of the edge test on the sphere around a vertex.
        private const int SphereDirectionCount = 24;
        private static readonly int FractureVolumeId = Shader.PropertyToID("_FractureVolume");
        private static readonly int FractureVolumeMinId = Shader.PropertyToID("_FractureVolumeMin");
        private static readonly int FractureVolumeScaleId = Shader.PropertyToID("_FractureVolumeScale");
        private static readonly int FractureVolumeRangeId = Shader.PropertyToID("_FractureVolumeRange");
        // Layout of RockFaceChunks without the cover channel, and the surface masks in UV 2. The Looga rock shaders read
        // these channels.
        private static readonly VertexAttributeDescriptor[] VertexLayout =
        {
            new(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new(VertexAttribute.Normal, VertexAttributeFormat.Float16, 4),
            new(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new(VertexAttribute.TexCoord1, VertexAttributeFormat.Float16, 2),
            new(VertexAttribute.TexCoord2, VertexAttributeFormat.UNorm8, 4),
            new(VertexAttribute.TexCoord3, VertexAttributeFormat.Float16, 2)
        };

        // Field normals stay on smooth parts: they hold detail of the full grid on all levels. A corner beside a hard edge
        // gets the mean normal of the faces on its side of the edge, so a sharp edge also looks sharp. Smooth vertex
        // normals across a crease make a sharp mesh look round (the usual fix for dual contouring meshes).
        [BurstCompile]
        private struct RockCreaseJob : IJob
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

        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex
        {
            public Vector3 Position;
            public half4 Normal;
            public Color32 Color;
            public half2 GroundOffset;
            public Color32 Masks;
            // x is the cave shade, 0 on a rock. y is the distance to the nearest crack line, for the rock shader.
            public half2 CrackLine;
        }

        [SerializeField] private RockRecipe _recipe = new();

        [Header("Mesh")]
        [Tooltip("How each grid cell places its vertex. Mean stays clean on detailed rock. Feature keeps sharper corners, but " +
                 "cells along a crease that crosses the grid can disagree and saw the crease into spikes.")]
        [SerializeField] private RockVertexPlacement _vertexPlacement = RockVertexPlacement.Mean;

        [Header("Levels Of Detail")]
        [Tooltip("Largest quadric error of each level, in meters. Each level starts from the level before it.")]
        [SerializeField] private float[] _levelErrors = { 0.01f, 0.03f, 0.1f, 0.3f };
        [Tooltip("Largest triangle count of each level. A level continues past its error until it fits. 0 = no budget.")]
        [SerializeField] private int[] _levelTriangles = { 160000, 60000, 18000, 4500 };
        [Range(0f, 90f), Tooltip("Faces that meet at more than this angle show a hard edge, in degrees. 0 turns this off.")]
        [SerializeField] private float _creaseAngle = 50f;
        [Tooltip("Number of levels with hard edges. Far levels have large faces and look better smooth.")]
        [SerializeField] private int _creaseLevels = 2;
        [Tooltip("Screen height where each level ends. Use one value for each level.")]
        [SerializeField] private float[] _levelScreenHeights = { 0.4f, 0.15f, 0.05f, 0.01f };

        [Header("Surface")]
        [Tooltip("Use a triplanar rock material, for example Looga/Rock Face.")]
        [SerializeField] private Material _material;

        private readonly List<Mesh> _meshes = new();
        private Texture3D _fractureVolume;

        /// <summary>The report of the last build. Null before the first build of this session.</summary>
        public RockBuildReport LastReport { get; private set; }

        /// <summary>The shape of this rock. Call <see cref="Rebuild"/> after a change.</summary>
        public RockRecipe Recipe
        {
            get => _recipe;
            set => _recipe = value;
        }

        /// <summary>Faces that meet at more than this angle show a hard edge. Call <see cref="Rebuild"/> after a change.</summary>
        public float CreaseAngle
        {
            get => _creaseAngle;
            set => _creaseAngle = value;
        }

        /// <summary>How each grid cell places its vertex. Call <see cref="Rebuild"/> after a change.</summary>
        public RockVertexPlacement VertexPlacement
        {
            get => _vertexPlacement;
            set => _vertexPlacement = value;
        }

        /// <summary>Largest quadric error of each level, in meters. Call <see cref="Rebuild"/> after a change.</summary>
        public float[] LevelErrors
        {
            get => _levelErrors;
            set => _levelErrors = value;
        }

        /// <summary>Largest triangle count of each level. Call <see cref="Rebuild"/> after a change.</summary>
        public int[] LevelTriangles
        {
            get => _levelTriangles;
            set => _levelTriangles = value;
        }

        /// <summary>The material of all levels of detail. Call <see cref="Rebuild"/> after a change.</summary>
        public Material Material
        {
            get => _material;
            set => _material = value;
        }

        #region Built-in
        private void OnEnable()
        {
            Transform output = transform.Find(OutputName);
            if (output != null)
            {
                // Existing meshes survive a script reload. The list of this object does not.
                CollectMeshes(output);
                return;
            }
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                // A scene load is in progress here. The build waits for the editor to finish it.
                UnityEditor.EditorApplication.delayCall += RebuildIfEmpty;
                return;
            }
#endif
            Rebuild();
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.delayCall -= RebuildIfEmpty;
#endif
            DestroyMeshes();
        }
        #endregion

        #region Build
        /// <summary>Builds the field, the mesh and the levels of detail again.</summary>
        [ContextMenu("Rebuild")]
        public void Rebuild()
        {
            float cell = _recipe.CellSize;
            Vector3 rockSize = _recipe.Size;
            List<RockFracture> fractures = AllFractures();
            float3 extents = (float3)(Vector3)rockSize * 0.5f;
            float passReach = 0f;
            foreach (RockPass pass in _recipe.Passes) passReach += pass.ReachOn(rockSize);
            foreach (RockBaseBulge bulge in _recipe.BaseBulges) passReach += Mathf.Max(bulge.Height, 0f);
            bool authored = _recipe.BaseBoxes.Count > 0;
            int blockCount = authored ? _recipe.BaseBoxes.Count : _recipe.BlockCount;
            float4[] planes = authored
                ? RockField.MakeBoxPlanes(_recipe.BaseBoxes)
                : RockField.MakeBlockPlanes(extents, blockCount, _recipe.FacesPerBlock, (uint)_recipe.Seed);

            Stopwatch timer = Stopwatch.StartNew();
            using RockBlockLayout blocks = authored && _recipe.BlockSettings.Spacing > 0f && _recipe.BlockFractures.Count > 0
                ? new RockBlockLayout(_recipe.BlockSettings, _recipe.BaseBoxes[0].Center, _recipe.BaseBoxes[0].Size, _recipe.BlockFractures,
                    _recipe.MassFractures, _recipe.FaceDepths, _recipe.BlockChannels)
                : null;
            if (blocks != null) UnityEngine.Debug.Log($"Looga Rock Generator: {blocks.MassCount} masses, {blocks.BlockCount} blocks, {blocks.ChipCount} chips in {timer.ElapsedMilliseconds} ms.", this);
            if (!FindBounds(planes, blockCount, extents + passReach, blocks, fractures, out float3 low, out float3 high)) return;
            float pad = 2f * BoundsCellScale * cell + GridMargin * cell;
            var size = (int3)math.ceil((high - low + 2f * pad) / cell) + 1;
            if ((long)size.x * size.y * size.z > MaximumSamples)
            {
                UnityEngine.Debug.LogError($"Looga Rock Generator: the grid {size} is over the limit of {MaximumSamples} " +
                                           "samples. Use a larger cell size.", this);
                return;
            }

            ClearOutput();
            var report = new RockBuildReport();
            NativeArray<float> field;
            NativeArray<float> tags;
            float3 origin;
            while (true)
            {
                size = (int3)math.ceil((high - low + 2f * pad) / cell) + 1;
                origin = low - pad;
                field = RockField.Build(planes, blockCount, size, origin, cell, rockSize, _recipe.BaseSlices, _recipe.BaseBulges,
                    _recipe.Passes, fractures, _recipe.MaxCarve, blocks, out tags, _recipe.ImportedField);
                // The coarse build can miss thin parts. Rock at the grid edge has no surface there, so the mesh has a hole.
                pad *= 2f;
                bool roomForMore = (long)(size.x + 2) * (size.y + 2) * (size.z + 2) * 2L <= MaximumSamples;
                if (!TouchesGridEdge(field, size)) break;
                if (!roomForMore)
                {
                    UnityEngine.Debug.LogWarning("Looga Rock Generator: the rock reaches the grid edge and the mesh has " +
                                                 "holes. Use a larger cell size.", this);
                    break;
                }
                field.Dispose();
                tags.Dispose();
            }
            report.GridSize = new Vector3Int(size.x, size.y, size.z);
            report.FieldSeconds = timer.Elapsed.TotalSeconds;

            timer.Restart();
            report.RemovedIslands = RockDualContour.CleanIslands(field, size);
            report.RemovedNeckedParts = RockDualContour.RemoveNeckedParts(field, size, cell, _recipe.NeckWidth);
            // A removed neck can leave a chip or an air pocket.
            if (report.RemovedNeckedParts > 0) report.RemovedIslands += RockDualContour.CleanIslands(field, size);
            RockDualContour.Build(field, size, _vertexPlacement, out NativeList<float3> positions,
                out NativeList<int> indices);
            report.ContourSeconds = timer.Elapsed.TotalSeconds;
            report.SourceTriangles = indices.Length / 3;

            timer.Restart();
            RockMaskSettings maskSettings = _recipe.Masks;
            var normals = new NativeArray<float3>(positions.Length, Allocator.TempJob);
            var cavities = new NativeArray<float>(positions.Length, Allocator.TempJob);
            var masks = new NativeArray<float4>(positions.Length, Allocator.TempJob);
            var sphere = new NativeArray<float3>(SphereDirectionCount, Allocator.TempJob);
            for (int index = 0; index < SphereDirectionCount; index++)
            {
                // Fibonacci sphere: an even spread with no line along the grid axes.
                float height = 1f - (2f * index + 1f) / SphereDirectionCount;
                float ring = math.sqrt(1f - height * height);
                float angle = index * 2.39996323f;
                sphere[index] = new float3(ring * math.cos(angle), height, ring * math.sin(angle));
            }
            new RockSurfaceMaskJob
            {
                Field = field, Positions = positions.AsArray(), SphereDirections = sphere, Normals = normals,
                Cavities = cavities, Masks = masks, Size = size, FieldScale = 1f / cell,
                EdgeReach = maskSettings.EdgeReach / cell,
                RidgeReach = maskSettings.RidgeReach / cell, CavityReach = maskSettings.CavityReach / cell,
                ShelterReach = maskSettings.ShelterReach / cell
            }.Schedule(positions.Length, 1024).Complete();

            var vertices = new NativeArray<Vertex>(positions.Length, Allocator.TempJob);
            var points = new NativeArray<float3>(positions.Length, Allocator.TempJob);
            var details = new NativeArray<float>(positions.Length, Allocator.TempJob);
            for (int index = 0; index < positions.Length; index++)
            {
                float3 point = origin + positions[index] * cell;
                float3 normal = normals[index];
                float4 mask = masks[index];
                points[index] = point;
                details[index] = (mask.x - mask.z) * maskSettings.MaskError;
                vertices[index] = new Vertex
                {
                    Position = point,
                    Normal = new half4((half)normal.x, (half)normal.y, (half)normal.z, (half)1f),
                    // Hardness is neutral. Blue holds the random value of the block. With World Strata off, the shader
                    // reads it as the layer value, so each block gets its own color between the layer colors.
                    Color = new Color32(128, (byte)(cavities[index] * 255f), BlockTag(tags, size, positions[index]), 0),
                    GroundOffset = default,
                    Masks = new Color32((byte)(mask.x * 255f), (byte)(mask.y * 255f), (byte)(mask.z * 255f),
                        (byte)(mask.w * 255f))
                };
            }
            float[] crackLines = RockField.CrackLineDistances(planes, blockCount, rockSize, _recipe.Passes, points, normals);
            for (int index = 0; index < vertices.Length; index++)
            {
                Vertex vertex = vertices[index];
                vertex.CrackLine = new half2((half)0f, (half)(crackLines != null ? crackLines[index] : NoCrackLine));
                vertices[index] = vertex;
            }
            report.VertexSeconds = timer.Elapsed.TotalSeconds;

            BuildLevels(points, details, vertices, indices.AsArray(), report);
            ApplyFractureVolume(origin, (float3)(size - 1) * cell, fractures, report);

            normals.Dispose();
            cavities.Dispose();
            masks.Dispose();
            sphere.Dispose();
            points.Dispose();
            details.Dispose();
            vertices.Dispose();
            positions.Dispose();
            indices.Dispose();
            field.Dispose();
            tags.Dispose();
            LastReport = report;
            UnityEngine.Debug.Log($"Looga Rock Generator: {report}", this);
        }

        // The volume belongs to this rock, so it goes on the renderers and not on the shared material. The rock mesh is in rock
        // space, so the volume stays on the rock when the rock moves.
        private void ApplyFractureVolume(float3 low, float3 extent, List<RockFracture> fractures, RockBuildReport report)
        {
            float cell = _recipe.FractureVolumeCell;
            if (cell <= 0f || fractures.Count == 0) return;
            int seed = 0;
            RockFractureSettings settings = default;
            foreach (RockPass pass in _recipe.Passes)
            {
                if (pass.Kind != RockPassKind.Fractures || pass.Skip) continue;
                seed = pass.Seed;
                settings = pass.Fractures;
            }

            _fractureVolume = RockField.BuildFractureVolume(fractures, seed, low, extent, cell, out int3 samples,
                out Vector4 ranges, settings);
            report.VolumeSize = new Vector3Int(samples.x, samples.y, samples.z);
            // A texture sample lies at the center of its texel.
            float3 corner = low - 0.5f * cell;
            float3 scale = 1f / ((float3)samples * cell);
            var block = new MaterialPropertyBlock();
            block.SetTexture(FractureVolumeId, _fractureVolume);
            block.SetVector(FractureVolumeMinId, new Vector4(corner.x, corner.y, corner.z, 0f));
            block.SetVector(FractureVolumeScaleId, new Vector4(scale.x, scale.y, scale.z, 1f));
            block.SetVector(FractureVolumeRangeId, ranges);
            foreach (MeshRenderer meshRenderer in transform.Find(OutputName).GetComponentsInChildren<MeshRenderer>())
            {
                meshRenderer.SetPropertyBlock(block);
            }
        }

        // The authored fractures and the fractures of the sets. The sets do not change the recipe.
        private List<RockFracture> AllFractures()
        {
            if (_recipe.FractureSets.Count == 0) return _recipe.Fractures;
            var fractures = new List<RockFracture>(_recipe.Fractures);
            Vector3 box = _recipe.Size * FractureBoxShare;
            foreach (RockFractureSet set in _recipe.FractureSets)
            {
                RockFractureNetwork.Add(set, box, fractures);
            }
            // A through fracture runs on to an older fracture or out of the box. Other fractures grow a short way only.
            bool through = _recipe.FractureSets.Exists(set => set.Through);
            RockFractureNetwork.Connect(fractures, box, through ? box.magnitude : FractureReach, FractureEndFade,
                _recipe.Fractures.Count);
            return fractures;
        }

        // Builds the rock on a coarse grid and returns the box of its rock samples.
        private bool FindBounds(float4[] planes, int blockCount, float3 reach, RockBlockLayout blocks,
            List<RockFracture> fractures, out float3 low, out float3 high)
        {
            float cell = _recipe.CellSize * BoundsCellScale;
            var size = (int3)math.ceil(2f * reach / cell) + 1;
            float3 origin = -reach;
            NativeArray<float> field = RockField.Build(planes, blockCount, size, origin, cell, _recipe.Size,
                _recipe.BaseSlices, _recipe.BaseBulges, _recipe.Passes, fractures, _recipe.MaxCarve, blocks,
                out NativeArray<float> tags, _recipe.ImportedField);
            tags.Dispose();

            low = float.MaxValue;
            high = float.MinValue;
            for (int z = 0; z < size.z; z++)
            {
                for (int y = 0; y < size.y; y++)
                {
                    for (int x = 0; x < size.x; x++)
                    {
                        if (field[x + size.x * (y + size.y * z)] >= 0f) continue;
                        float3 point = origin + new float3(x, y, z) * cell;
                        low = math.min(low, point);
                        high = math.max(high, point);
                    }
                }
            }
            field.Dispose();
            if (math.all(low <= high)) return true;
            UnityEngine.Debug.LogError("Looga Rock Generator: the passes left no rock.", this);
            return false;
        }

        private static bool TouchesGridEdge(NativeArray<float> field, int3 size)
        {
            for (int z = 0; z < size.z; z++)
            {
                bool outerZ = z == 0 || z == size.z - 1;
                for (int y = 0; y < size.y; y++)
                {
                    bool outerY = y == 0 || y == size.y - 1;
                    for (int x = 0; x < size.x; x++)
                    {
                        if (!outerZ && !outerY && x > 0 && x < size.x - 1)
                        {
                            // Only the last sample of this row lies on the edge.
                            x = size.x - 2;
                            continue;
                        }
                        if (field[x + size.x * (y + size.y * z)] < 0f) return true;
                    }
                }
            }
            return false;
        }

        // The tag of the nearest sample. Tags are block values, so a blend of samples has no meaning.
        private static byte BlockTag(NativeArray<float> tags, int3 size, float3 position)
        {
            var cell = (int3)math.round(position);
            cell = math.clamp(cell, 0, size - 1);
            return (byte)(tags[cell.x + size.x * (cell.y + size.y * cell.z)] * 255f);
        }

        private void BuildLevels(NativeArray<float3> points, NativeArray<float> details, NativeArray<Vertex> vertices,
            NativeArray<int> source, RockBuildReport report)
        {
            int levelCount = _levelErrors.Length;
            report.LevelSeconds = new double[levelCount];
            report.LevelTriangles = new int[levelCount];
            report.LevelVertices = new int[levelCount];
            var levels = new LOD[levelCount];

            var output = new GameObject(OutputName) { hideFlags = HideFlags.DontSave };
            output.transform.SetParent(transform, false);
            Stopwatch timer = Stopwatch.StartNew();
            NativeList<int> previous = default;
            for (int level = 0; level < levelCount; level++)
            {
                timer.Restart();
                double errorLimit = _levelErrors[level] * (double)_levelErrors[level];
                NativeArray<int> input = level == 0 ? source : previous.AsArray();
                int budget = level < _levelTriangles.Length && _levelTriangles[level] > 0 ? _levelTriangles[level]
                    : int.MaxValue;
                NativeList<int> reduced = RockDecimator.Reduce(points, details, input, errorLimit, budget);
                report.LevelSeconds[level] = timer.Elapsed.TotalSeconds;
                if (previous.IsCreated) previous.Dispose();
                previous = reduced;

                timer.Restart();
                float crease = level < _creaseLevels ? _creaseAngle : 0f;
                Mesh mesh = CreateMesh($"Rock LOD{level}", points, vertices, reduced.AsArray(), crease);
                _meshes.Add(mesh);
                report.MeshSeconds += timer.Elapsed.TotalSeconds;
                report.LevelTriangles[level] = (int)mesh.GetIndexCount(0) / 3;
                report.LevelVertices[level] = mesh.vertexCount;

                var child = new GameObject($"LOD{level}") { hideFlags = HideFlags.DontSave };
                child.transform.SetParent(output.transform, false);
                child.AddComponent<MeshFilter>().sharedMesh = mesh;
                var meshRenderer = child.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterial = _material;
                levels[level] = new LOD(_levelScreenHeights[Mathf.Min(level, _levelScreenHeights.Length - 1)],
                    new Renderer[] { meshRenderer });
            }
            if (previous.IsCreated) previous.Dispose();
            output.AddComponent<LODGroup>().SetLODs(levels);
        }

        // Keeps only the vertices of the kept triangles. Vertices on a hard edge split, one copy for each side of the edge.
        private static Mesh CreateMesh(string meshName, NativeArray<float3> points, NativeArray<Vertex> vertices,
            NativeArray<int> triangles, float creaseAngle)
        {
            var kept = new NativeList<Vertex>(vertices.Length, Allocator.TempJob);
            var indices = new NativeArray<int>(triangles.Length, Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);
            new RockCreaseJob
            {
                Points = points, Vertices = vertices, Triangles = triangles, Kept = kept, Indices = indices,
                CreaseCosine = creaseAngle > 0f ? math.cos(math.radians(creaseAngle)) : -2f
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
        #endregion

        #region Output
#if UNITY_EDITOR
        private void RebuildIfEmpty()
        {
            UnityEditor.EditorApplication.delayCall -= RebuildIfEmpty;
            if (this == null || !isActiveAndEnabled || transform.Find(OutputName) != null) return;
            Rebuild();
        }
#endif

        private void CollectMeshes(Transform output)
        {
            _meshes.Clear();
            foreach (MeshFilter filter in output.GetComponentsInChildren<MeshFilter>())
            {
                _meshes.Add(filter.sharedMesh);
            }

            // The renderers keep the volume over a script reload. This field does not.
            var block = new MaterialPropertyBlock();
            MeshRenderer first = output.GetComponentInChildren<MeshRenderer>();
            if (first == null) return;
            first.GetPropertyBlock(block);
            _fractureVolume = block.GetTexture(FractureVolumeId) as Texture3D;
        }

        private void ClearOutput()
        {
            DestroyMeshes();
            Transform output = transform.Find(OutputName);
            if (output == null) return;
            if (Application.isPlaying)
            {
                Destroy(output.gameObject);
            }
            else
            {
                DestroyImmediate(output.gameObject);
            }
        }

        private void DestroyMeshes()
        {
            foreach (Mesh mesh in _meshes)
            {
                if (mesh == null) continue;
                if (Application.isPlaying)
                {
                    Destroy(mesh);
                }
                else
                {
                    DestroyImmediate(mesh);
                }
            }
            _meshes.Clear();
            if (_fractureVolume == null) return;
            if (Application.isPlaying)
            {
                Destroy(_fractureVolume);
            }
            else
            {
                DestroyImmediate(_fractureVolume);
            }
            _fractureVolume = null;
        }
        #endregion
    }
}
