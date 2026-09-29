using LoogaSoft.Instancing;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Instances
{
    /// <summary>Execution path for the same deterministic detail distribution.</summary>
    public enum DetailGenerationBackend { Reference, Burst, Compute }

    /// <summary>Original Looga detail placement using native density, size, holes and alignment inputs.</summary>
    /// <remarks>Its distribution is stable across backends but is not Unity's private placement noise.
    /// Call RefreshSurface after height or hole edits. Detail paint is read for each requested page.
    /// GPU results use asynchronous readback before the existing budgeted BRG upload.</remarks>
    public sealed class GeneratedVegetationPageSource : IVegetationPageSource
    {
        private readonly UnityEngine.Terrain _terrain;
        private readonly DetailGenerationBackend _backend;
        private readonly int _maximumRecords;
        private float[] _heights;
        private uint[] _holes;
        private int _resolution;
        private int _holeResolution;
        private uint _revision;
        public DetailGenerationBackend Backend => _backend;
        public uint SurfaceRevision => _revision;
        private struct Candidate { public uint Cell; public uint Ordinal; }
        private struct Placement { public float4 Position; public float4 Scale; public float4 Normal; }

        /// <summary>Capture the surface. Construct and load on Unity's main thread.</summary>
        public GeneratedVegetationPageSource(UnityEngine.Terrain terrain, DetailGenerationBackend backend, int maximumRecords = 262144)
        {
            if (!terrain || !terrain.terrainData || maximumRecords < 1) throw new ArgumentException("A terrain and positive page limit are required.");
            _terrain = terrain;
            _backend = backend;
            _maximumRecords = maximumRecords;
            RefreshSurface();
        }

        /// <summary>Replace the surface snapshot and reject results from the old revision.</summary>
        public void RefreshSurface(bool invalidatePending = true)
        {
            var data = _terrain.terrainData;
            _resolution = data.heightmapResolution;
            _holeResolution = data.holesResolution;
            var heights = data.GetHeights(0, 0, _resolution, _resolution);
            var holes = data.GetHoles(0, 0, _holeResolution, _holeResolution);
            _heights = new float[heights.Length];
            Buffer.BlockCopy(heights, 0, _heights, 0, heights.Length * sizeof(float));
            _holes = new uint[holes.Length];
            for (int z = 0; z < _holeResolution; z++)
            {
                for (int x = 0; x < _holeResolution; x++)
                {
                    _holes[z * _holeResolution + x] = holes[z, x] ? 1u : 0u;
                }
            }
            if (invalidatePending)
            {
                _revision++;
            }
        }

        /// <summary>Refresh only changed height samples. Coordinates use heightmap texels.</summary>
        public void RefreshHeights(RectInt region, bool invalidatePending = true)
        {
            var data = _terrain.terrainData;
            RectInt bounded = Clamp(region, _resolution, _resolution);
            if (bounded.width == 0 || bounded.height == 0) return;
            float[,] values = data.GetHeights(bounded.x, bounded.y, bounded.width, bounded.height);
            for (int z = 0; z < bounded.height; z++)
                for (int x = 0; x < bounded.width; x++)
                    _heights[(bounded.y + z) * _resolution + bounded.x + x] = values[z, x];
            if (invalidatePending) _revision++;
        }

        /// <summary>Refresh only changed hole samples. Coordinates use hole-map texels.</summary>
        public void RefreshHoles(RectInt region, bool invalidatePending = true)
        {
            var data = _terrain.terrainData;
            RectInt bounded = Clamp(region, _holeResolution, _holeResolution);
            if (bounded.width == 0 || bounded.height == 0) return;
            bool[,] values = data.GetHoles(bounded.x, bounded.y, bounded.width, bounded.height);
            for (int z = 0; z < bounded.height; z++)
                for (int x = 0; x < bounded.width; x++)
                    _holes[(bounded.y + z) * _holeResolution + bounded.x + x] = values[z, x] ? 1u : 0u;
            if (invalidatePending) _revision++;
        }

        private static RectInt Clamp(RectInt region, int width, int height)
        {
            int x0 = Mathf.Clamp(region.xMin, 0, width);
            int y0 = Mathf.Clamp(region.yMin, 0, height);
            int x1 = Mathf.Clamp(region.xMax, 0, width);
            int y1 = Mathf.Clamp(region.yMax, 0, height);
            return new RectInt(x0, y0, Mathf.Max(0, x1 - x0), Mathf.Max(0, y1 - y0));
        }

        /// <summary>Generate a bounded page. Trees retain the native placement records.</summary>
        public async Task<VegetationPageRecord[]> LoadAsync(Vector2Int cell, bool trees, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var data = _terrain.terrainData;
            uint revision = _revision;
            var result = new List<VegetationPageRecord>();
            if (trees)
            {
                foreach (var tree in data.treeInstances)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Vector3 position = Vector3.Scale(tree.position, data.size);
                    if (Mathf.FloorToInt(position.x / 64) != cell.x || Mathf.FloorToInt(position.z / 64) != cell.y)
                    {
                        continue;
                    }
                    if (result.Count == _maximumRecords) throw new InvalidOperationException("Tree page exceeds its record budget.");
                    result.Add(new VegetationPageRecord(tree.prototypeIndex,
                        Matrix4x4.TRS(position, Quaternion.Euler(0, tree.rotation * Mathf.Rad2Deg, 0), new Vector3(tree.widthScale, tree.heightScale, tree.widthScale)),
                        new InstanceAppearance(tree.color, tree.lightmapColor, Vector4.zero)));
                }
                return result.ToArray();
            }
            if (cell.x < 0 || cell.y < 0 || cell.x >= data.detailPatchCount || cell.y >= data.detailPatchCount)
            {
                throw new ArgumentOutOfRangeException(nameof(cell));
            }
            int width = data.detailResolutionPerPatch;
            var prototypes = data.detailPrototypes;
            for (int layer = 0; layer < prototypes.Length; layer++)
            {
                cancellation.ThrowIfCancellationRequested();
                var prototype = prototypes[layer];
                var density = data.GetDetailLayer(cell.x * width, cell.y * width, width, width, layer);
                float multiplier = DensityMultiplier(_terrain, layer);
                var candidates = new List<Candidate>();
                uint seed = unchecked((uint)prototype.noiseSeed);
                for (int z = 0; z < width; z++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        uint key = (uint)((cell.y * width + z) * data.detailWidth + cell.x * width + x);
                        int count = CandidateCount(density[z, x], multiplier, key, seed);
                        if ((long)candidates.Count + count + result.Count > _maximumRecords)
                        {
                            throw new InvalidOperationException("Detail page exceeds its record budget.");
                        }
                        for (uint i = 0; i < count; i++)
                        {
                            candidates.Add(new Candidate { Cell = key, Ordinal = i });
                        }
                    }
                }
                if (candidates.Count == 0)
                {
                    continue;
                }
                var input = new NativeArray<Candidate>(candidates.ToArray(), Allocator.Persistent);
                var heights = new NativeArray<float>(_heights, Allocator.Persistent);
                var holes = new NativeArray<uint>(_holes, Allocator.Persistent);
                var output = new NativeArray<Placement>(input.Length, Allocator.Persistent);
                var job = new GenerateJob
                {
                    Input = input, Heights = heights, Holes = holes, Output = output,
                    Resolution = _resolution, HoleResolution = _holeResolution, DetailWidth = data.detailWidth,
                    Size = data.size, Seed = seed,
                    Sizes = new float4(prototype.minWidth, prototype.maxWidth, prototype.minHeight, prototype.maxHeight),
                    HolePadding = math.max(0, prototype.holeEdgePadding)
                };
                JobHandle handle = default;
                try
                {
                    if (_backend == DetailGenerationBackend.Compute)
                    {
                        Placement[] generated = await GenerateGpu(job, cancellation);
                        output.CopyFrom(generated);
                    }
                    else if (_backend == DetailGenerationBackend.Burst)
                    {
                        handle = job.Schedule(input.Length, 64);
                        JobHandle.ScheduleBatchedJobs();
                        while (!handle.IsCompleted)
                        {
                            await Task.Yield();
                        }
                        handle.Complete();
                    }
                    else
                    {
                        for (int i = 0; i < input.Length; i++)
                        {
                            job.Execute(i);
                        }
                    }
                    cancellation.ThrowIfCancellationRequested();
                    if (_revision != revision) throw new OperationCanceledException("The source surface changed.");
                    for (int i = 0; i < output.Length; i++)
                    {
                        Placement item = output[i];
                        if (item.Scale.w == 0)
                        {
                            continue;
                        }
                        Quaternion rotation = Quaternion.Slerp(Quaternion.identity,
                            Quaternion.FromToRotation(Vector3.up, item.Normal.xyz), prototype.alignToGround) * Quaternion.Euler(0, item.Position.w * Mathf.Rad2Deg, 0);
                        result.Add(new VegetationPageRecord(layer,
                            Matrix4x4.TRS(item.Position.xyz, rotation, new Vector3(item.Scale.x, item.Scale.y, item.Scale.x)),
                            DetailPrototypeResources.Appearance(item.Position.xyz, prototype)));
                    }
                }
                finally
                {
                    handle.Complete();
                    input.Dispose(); heights.Dispose(); holes.Dispose(); output.Dispose();
                }
            }
            return result.ToArray();
        }

        private static async Task<Placement[]> GenerateGpu(GenerateJob job, CancellationToken cancellation)
        {
            var shader = Resources.Load<ComputeShader>("LoogaDetailGeneration");
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback || !shader)
            {
                throw new NotSupportedException("Compute detail generation requires compute and asynchronous GPU readback.");
            }
            using var input = new GraphicsBuffer(GraphicsBuffer.Target.Structured, job.Input.Length, 8);
            using var heights = new GraphicsBuffer(GraphicsBuffer.Target.Structured, job.Heights.Length, 4);
            using var holes = new GraphicsBuffer(GraphicsBuffer.Target.Structured, job.Holes.Length, 4);
            using var output = new GraphicsBuffer(GraphicsBuffer.Target.Structured, job.Output.Length, 48);
            input.SetData(job.Input); heights.SetData(job.Heights); holes.SetData(job.Holes);
            int kernel = shader.FindKernel("Generate");
            shader.SetBuffer(kernel, "_Candidates", input); shader.SetBuffer(kernel, "_Heights", heights);
            shader.SetBuffer(kernel, "_Holes", holes); shader.SetBuffer(kernel, "_Output", output);
            shader.SetInts("_Layout", job.Resolution, job.HoleResolution, job.DetailWidth, job.Input.Length);
            shader.SetInt("_Seed", unchecked((int)job.Seed));
            shader.SetVector("_Size", (Vector3)job.Size); shader.SetVector("_Sizes", (Vector4)job.Sizes);
            shader.SetFloat("_HolePadding", job.HolePadding);
            shader.Dispatch(kernel, (job.Input.Length + 63) / 64, 1, 1);
            var completion = new TaskCompletionSource<Placement[]>();
            AsyncGPUReadback.Request(output, request =>
            {
                if (request.hasError) completion.TrySetException(new InvalidOperationException("Detail generation readback failed."));
                else completion.TrySetResult(request.GetData<Placement>().ToArray());
            });
            var result = await completion.Task;
            cancellation.ThrowIfCancellationRequested();
            return result;
        }

        // Shaders/DetailPlacement.hlsl holds the GPU copy of Hash and Random. Keep both copies equal.
        internal static uint Hash(uint value)
        {
            value ^= value >> 16; value *= 0x7feb352du; value ^= value >> 15;
            value *= 0x846ca68bu; return value ^ (value >> 16);
        }
        internal static float Random(uint value) => (Hash(value) & 0x00ffffffu) * (1f / 16777216f);

        /// <summary>Expected instances for each unit of painted density in one detail texel.</summary>
        internal static float DensityMultiplier(UnityEngine.Terrain terrain, int layer)
        {
            TerrainData data = terrain.terrainData;
            float multiplier = data.detailPrototypes[layer].useDensityScaling ? terrain.detailObjectDensity : 1;
            if (data.detailScatterMode == DetailScatterMode.CoverageMode)
            {
                multiplier *= data.ComputeDetailCoverage(layer) * data.size.x * data.size.z /
                    (data.detailWidth * (float)data.detailHeight * 255);
            }
            return multiplier;
        }

        /// <summary>Candidates in one detail texel, before hole rejection.</summary>
        internal static int CandidateCount(int density, float multiplier, uint key, uint seed)
        {
            float expected = density * multiplier;
            return (int)math.floor(expected) + (Random(key ^ seed) < math.frac(expected) ? 1 : 0);
        }

        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        private struct GenerateJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Candidate> Input;
            [ReadOnly] public NativeArray<float> Heights;
            [ReadOnly] public NativeArray<uint> Holes;
            public NativeArray<Placement> Output;
            public int Resolution, HoleResolution, DetailWidth;
            public float3 Size;
            public float4 Sizes;
            public uint Seed;
            public float HolePadding;

            private float Height(float2 uv)
            {
                float2 point = math.saturate(uv) * (Resolution - 1);
                int2 lo = (int2)math.floor(point);
                int2 hi = math.min(lo + 1, Resolution - 1);
                float2 t = math.frac(point);
                return math.lerp(math.lerp(Heights[lo.y * Resolution + lo.x], Heights[lo.y * Resolution + hi.x], t.x),
                    math.lerp(Heights[hi.y * Resolution + lo.x], Heights[hi.y * Resolution + hi.x], t.x), t.y) * Size.y;
            }
            public void Execute(int index)
            {
                Candidate candidate = Input[index];
                uint seed = Hash(candidate.Cell ^ Seed) ^ Hash(candidate.Ordinal + 1);
                float2 cell = new float2(candidate.Cell % (uint)DetailWidth, candidate.Cell / (uint)DetailWidth);
                // Placement fills the whole texel. A narrower spread leaves empty bands at texel edges, which show a grid.
                float2 uv = (cell + new float2(Random(seed), Random(seed + 1))) / DetailWidth;
                float2 delta = new float2(1f / (Resolution - 1));
                float3 normal = math.normalize(new float3((Height(uv - new float2(delta.x, 0)) - Height(uv + new float2(delta.x, 0))) / (2 * delta.x * Size.x),
                    1, (Height(uv - new float2(0, delta.y)) - Height(uv + new float2(0, delta.y))) / (2 * delta.y * Size.z)));
                bool valid = true;
                int2 center = math.clamp((int2)(uv * HoleResolution), 0, HoleResolution - 1);
                int2 padding = (int2)math.ceil(HolePadding * HoleResolution / Size.xz);
                for (int z = math.max(0, center.y - padding.y); z <= math.min(HoleResolution - 1, center.y + padding.y); z++)
                {
                    for (int x = math.max(0, center.x - padding.x); x <= math.min(HoleResolution - 1, center.x + padding.x); x++)
                    {
                        valid &= Holes[z * HoleResolution + x] != 0;
                    }
                }
                Output[index] = new Placement
                {
                    Position = new float4(uv.x * Size.x, Height(uv), uv.y * Size.z, Random(seed + 2) * (2 * math.PI)),
                    Scale = new float4(math.lerp(Sizes.x, Sizes.y, Random(seed + 3)), math.lerp(Sizes.z, Sizes.w, Random(seed + 4)), 0, valid ? 1 : 0),
                    Normal = new float4(normal, 0)
                };
            }
        }
    }
}
