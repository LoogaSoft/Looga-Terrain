using LoogaSoft.Instancing;
using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Instances
{
    // Places detail pages on the GPU with the GeneratedVegetationPageSource distribution.
    // The CPU reads painted density and counts candidates for each texel in a Burst job. The GPU does all work for each
    // instance.
    // Heights and holes come from the terrain textures, so the GPU reads the current surface.
    internal sealed class GpuDetailGenerator : IDisposable
    {
        private struct Page
        {
            internal uint TexelX;
            internal uint TexelZ;
            internal uint Layer;
            internal uint Unused;
            internal uint FirstCount;
            internal uint FirstOutput;
            internal uint OutputCount;
            internal uint Unused2;
        }

        private struct Layer
        {
            internal Vector4 Sizes;
            internal uint Seed;
            internal float HolePadding;
            internal float Alignment;
            internal float Unused;
        }

        // Dispatch limit for one grid dimension.
        private const int MaximumGroups = 65535;

        private readonly UnityEngine.Terrain _terrain;
        private readonly TerrainData _data;
        private readonly ComputeShader _shader;
        private readonly int _kernel;
        private readonly float[] _multipliers;
        private readonly uint[] _seeds;
        private readonly GraphicsBuffer _layers;
        private readonly List<Page> _pages = new();
        // Candidates for each queued texel, in page order. The count job appends to it; Dispatch uploads it.
        private NativeList<uint> _counts;
        // One page of painted density: the engine returns int[,], the job reads a flat native copy.
        private int[] _densityRows = Array.Empty<int>();
        private NativeArray<int> _density;
        private NativeArray<int> _pageTotal;
        private GraphicsBuffer _pageBuffer;
        private GraphicsBuffer _countBuffer;
        private GraphicsBuffer _placements;

        /// <summary>Placements queued since the last Clear. Each placement is one output transform.</summary>
        internal int PendingPlacements { get; private set; }

        /// <summary>Output of the last Dispatch. InstanceRenderer.WriteRanges reads it.</summary>
        internal GraphicsBuffer Placements => _placements;

        /// <summary>Largest axis scale of any placement. Instance ranges use it as their reach.</summary>
        internal float MaximumScale { get; }

        internal GpuDetailGenerator(UnityEngine.Terrain terrain)
        {
            _terrain = terrain;
            _data = terrain.terrainData;
            _shader = Resources.Load<ComputeShader>("LoogaDetailPlacement");
            if (!SystemInfo.supportsComputeShaders || !_shader)
            {
                throw new NotSupportedException("GPU detail placement requires compute shaders.");
            }
            _kernel = _shader.FindKernel("Place");
            DetailPrototype[] prototypes = _data.detailPrototypes;
            _multipliers = new float[prototypes.Length];
            _seeds = new uint[prototypes.Length];
            var layers = new Layer[Mathf.Max(1, prototypes.Length)];
            for (int layer = 0; layer < prototypes.Length; layer++)
            {
                DetailPrototype prototype = prototypes[layer];
                // Coverage mode reads the complete layer. The stream rebuilds this generator after a density change.
                _multipliers[layer] = GeneratedVegetationPageSource.DensityMultiplier(terrain, layer);
                _seeds[layer] = unchecked((uint)prototype.noiseSeed);
                layers[layer] = new Layer
                {
                    Sizes = new Vector4(prototype.minWidth, prototype.maxWidth, prototype.minHeight, prototype.maxHeight),
                    Seed = _seeds[layer],
                    HolePadding = Mathf.Max(0, prototype.holeEdgePadding),
                    Alignment = prototype.alignToGround
                };
                MaximumScale = Mathf.Max(MaximumScale, prototype.maxWidth, prototype.maxHeight);
            }
            _layers = new GraphicsBuffer(GraphicsBuffer.Target.Structured, layers.Length, 32);
            _layers.SetData(layers);
            // Allocate native memory last: a constructor exception above must not leak it.
            _counts = new NativeList<uint>(Allocator.Persistent);
            _pageTotal = new NativeArray<int>(1, Allocator.Persistent);
        }

        /// <summary>Queue one page. The page output starts at the returned offset.</summary>
        /// <returns>Placements in the page. Zero queues nothing.</returns>
        internal int AddPage(Vector2Int patch, int layer, out int firstOutput)
        {
            firstOutput = PendingPlacements;
            int side = _data.detailResolutionPerPatch;
            int x0 = patch.x * side;
            int z0 = patch.y * side;
            int[,] density = _data.GetDetailLayer(x0, z0, side, side, layer);
            int texels = side * side;
            if (_densityRows.Length != texels)
            {
                _densityRows = new int[texels];
                if (_density.IsCreated) _density.Dispose();
                _density = new NativeArray<int>(texels, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            // Row-major copies: density[z, x] becomes element z * side + x.
            Buffer.BlockCopy(density, 0, _densityRows, 0, texels * sizeof(int));
            _density.CopyFrom(_densityRows);
            int firstCount = _counts.Length;
            new CountJob
            {
                Density = _density,
                Counts = _counts,
                Total = _pageTotal,
                Side = side,
                TexelX = x0,
                TexelZ = z0,
                DetailWidth = _data.detailWidth,
                Multiplier = _multipliers[layer],
                Seed = _seeds[layer]
            }.Run();
            int total = _pageTotal[0];
            if (total == 0)
            {
                _counts.ResizeUninitialized(firstCount);
                return 0;
            }
            _pages.Add(new Page
            {
                TexelX = (uint)x0, TexelZ = (uint)z0, Layer = (uint)layer,
                FirstCount = (uint)firstCount, FirstOutput = (uint)firstOutput, OutputCount = (uint)total
            });
            PendingPlacements += total;
            return total;
        }

        /// <summary>Place all queued pages. The GPU runs this work before later submissions.</summary>
        internal void Dispatch()
        {
            if (_pages.Count == 0) return;
            Ensure(ref _pageBuffer, _pages.Count, 32);
            Ensure(ref _countBuffer, _counts.Length, 4);
            Ensure(ref _placements, PendingPlacements, InstanceRenderer.RangeTransformStride);
            Texture holes = _data.holesTexture;
            if (!holes)
            {
                holes = Texture2D.whiteTexture;
            }
            CommandBuffer cmd = CommandBufferPool.Get("Looga Detail Placement");
            try
            {
                cmd.SetBufferData(_pageBuffer, _pages);
                cmd.SetBufferData(_countBuffer, _counts.AsArray());
                cmd.SetComputeBufferParam(_shader, _kernel, "_Pages", _pageBuffer);
                cmd.SetComputeBufferParam(_shader, _kernel, "_Counts", _countBuffer);
                cmd.SetComputeBufferParam(_shader, _kernel, "_Layers", _layers);
                cmd.SetComputeBufferParam(_shader, _kernel, "_Placements", _placements);
                cmd.SetComputeTextureParam(_shader, _kernel, "_Heightmap", _data.heightmapTexture);
                cmd.SetComputeTextureParam(_shader, _kernel, "_Holes", holes);
                cmd.SetComputeVectorParam(_shader, "_TerrainSize", _data.size);
                cmd.SetComputeVectorParam(_shader, "_TerrainOrigin", _terrain.transform.position);
                cmd.SetComputeIntParams(_shader, "_PlacementLayout", _data.heightmapResolution, holes.width,
                    _data.detailWidth, _data.detailResolutionPerPatch);
                for (int first = 0; first < _pages.Count; first += MaximumGroups)
                {
                    cmd.SetComputeIntParam(_shader, "_PageBase", first);
                    cmd.DispatchCompute(_shader, _kernel, Mathf.Min(MaximumGroups, _pages.Count - first), 1, 1);
                }
                Graphics.ExecuteCommandBuffer(cmd);
            }
            finally
            {
                CommandBufferPool.Release(cmd);
            }
        }

        /// <summary>Forget queued pages. The placement buffer keeps its contents until the next Dispatch.</summary>
        internal void Clear()
        {
            _pages.Clear();
            _counts.Clear();
            PendingPlacements = 0;
        }

        private static void Ensure(ref GraphicsBuffer buffer, int count, int stride)
        {
            if (buffer != null && buffer.count >= count) return;
            // Unity releases the old buffer after submitted GPU work stops using it.
            buffer?.Dispose();
            buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(Mathf.Max(256, count)),
                stride);
        }

        public void Dispose()
        {
            _layers.Dispose();
            if (_counts.IsCreated) _counts.Dispose();
            if (_density.IsCreated) _density.Dispose();
            if (_pageTotal.IsCreated) _pageTotal.Dispose();
            _pageBuffer?.Dispose();
            _countBuffer?.Dispose();
            _placements?.Dispose();
            _pageBuffer = _countBuffer = _placements = null;
        }

        // Counts candidates for each texel of one page. The same float settings as GeneratedVegetationPageSource keep
        // the counts identical to the managed loop that this job replaced.
        [BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        private struct CountJob : IJob
        {
            [ReadOnly] public NativeArray<int> Density;
            public NativeList<uint> Counts;
            public NativeArray<int> Total;
            public int Side, TexelX, TexelZ, DetailWidth;
            public float Multiplier;
            public uint Seed;

            public void Execute()
            {
                int total = 0;
                for (int z = 0; z < Side; z++)
                {
                    for (int x = 0; x < Side; x++)
                    {
                        uint key = (uint)((TexelZ + z) * DetailWidth + TexelX + x);
                        int count = GeneratedVegetationPageSource.CandidateCount(Density[z * Side + x], Multiplier, key,
                            Seed);
                        Counts.Add((uint)count);
                        total += count;
                    }
                }
                Total[0] = total;
            }
        }
    }
}
